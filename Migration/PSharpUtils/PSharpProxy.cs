// MigratingTable
// Copyright (c) Microsoft Corporation; see license.txt

using System;
using System.Threading.Tasks;
using Microsoft.PSharp;
using System.Runtime.Remoting.Proxies;
using System.Runtime.Remoting.Messaging;
using System.Reflection;
using System.Diagnostics;
using System.Linq;
using ChainTableInterface;

namespace Migration
{
    public delegate Event EventFactory();

    /*
     * PSharpProxy proxies all async methods of an interface so that they
     * execute on the event loop of the host machine.  Replies are sent back to
     * the caller machine.
     *
     * Interfaces that take or return further shared objects (as opposed to
     * conceptually transferring ownership) will need custom proxies that wrap
     * those objects in proxies.  It seems to be easiest to wrap the PSharpProxy
     * transparent proxy in another object and write the methods that don't need
     * special handling as boilerplate passthroughs.  It looks like trying to
     * avoid that boilerplate by hooking Invoke to implement the special
     * handling in terms of the reflected call, or generating a subclass of the
     * transparent proxy class with some methods overridden, is going to be more
     * trouble than it's worth.
     */
    // http://stackoverflow.com/questions/15733900/dynamically-creating-a-proxy-class
    // Or (more authoritative?): https://msdn.microsoft.com/en-us/library/System.Runtime.Remoting.Messaging.IMethodMessage(v=vs.110).aspx
    class PSharpRealProxy : RealProxy
    {
        internal readonly MachineId callerMachineId;
        internal readonly MachineId hostMachineId;
        internal readonly object target;
        internal readonly string proxyName;
        // The anticipated use case is to control the event type (for selective
        // deferral, etc.) since we'll always overwrite the payload.
        readonly EventFactory eventFactory;

        static readonly Type typeofTaskGeneric = typeof(Task<object>).GetGenericTypeDefinition();

        // Important: TIface must be an interface (or subclass of MarshalByRefObject).
        // This is easy to mess up if you allow it to be inferred.
        public static TIface MakeTransparentProxy<TIface>(MachineId callerMachineId, MachineId hostMachineId,
            TIface target, string targetDebugName, EventFactory eventFactory)
        {
            // XXX: Verify up front that TIface is an interface with all methods acceptable?
            var realProxy = new PSharpRealProxy(typeof(TIface), callerMachineId, hostMachineId, target, targetDebugName, eventFactory);
            return (TIface)realProxy.GetTransparentProxy();
        }

        PSharpRealProxy(Type type, MachineId callerMachineId, MachineId hostMachineId,
            object target, string targetDebugName, EventFactory eventFactory) : base(type)
        {
            this.callerMachineId = callerMachineId;
            this.hostMachineId = hostMachineId;
            this.target = target;
            this.eventFactory = eventFactory;
            proxyName = (targetDebugName == null) ? null :
                string.Format("PSharpProxy from {0} to {1} for {2}", callerMachineId, hostMachineId, targetDebugName);
        }

        class Capturer : TypeCapturer<IMethodReturnMessage>
        {
            readonly PSharpRealProxy outer;
            readonly IMethodCallMessage callMsg;
            internal Capturer(PSharpRealProxy outer, IMethodCallMessage callMsg)
            {
                this.outer = outer;
                this.callMsg = callMsg;
            }
            public override IMethodReturnMessage Invoke<TResult>()
            {
                // TODO: Create a reply ID once we know what information we need to include in it.
                var replyTarget = new ReplyTarget<TResult>(null, outer.callerMachineId);
                PSharpRuntime.SendEvent(outer.hostMachineId, outer.eventFactory(),
                    new CallPayload<TResult>(outer, replyTarget, (MethodInfo)callMsg.MethodBase, callMsg.Args));
                return new ReturnMessage(replyTarget.tcs.Task, null, 0, callMsg.LogicalCallContext, callMsg);
            }
        }

        public override IMessage Invoke(IMessage msg)
        {
            // WARNING: Apparently msg contains unmanaged data that is only
            // valid during this call.  Dereferencing it asynchronously from the
            // other machine caused a segmentation fault!  This wasn't documented.
            IMethodCallMessage callMsg = (IMethodCallMessage)msg;
            MethodInfo mi = (MethodInfo)callMsg.MethodBase;
            Type returnTaskType = mi.ReturnType;

            // I wish we could pattern match here.
            Type returnResultType;
            if (returnTaskType == typeof(Task))
                returnResultType = typeof(Void2);  // See compensating code in CallPayload.Dispatch.
            else if (returnTaskType.GetGenericTypeDefinition().Equals(typeofTaskGeneric))
                returnResultType = returnTaskType.GenericTypeArguments[0];
            else
                throw new NotImplementedException();

            return new Capturer(this, callMsg).Capture(returnResultType);
        }
    }

    class CallPayload<TResult> : IDispatchable
    {
        readonly PSharpRealProxy realProxy;
        readonly ReplyTarget<TResult> replyTarget;
        readonly MethodInfo method;
        readonly object[] args;

        internal CallPayload(PSharpRealProxy realProxy, ReplyTarget<TResult> replyTarget, MethodInfo method, object[] args)
        {
            this.realProxy = realProxy;
            this.replyTarget = replyTarget;
            this.method = method;
            this.args = args;
        }

        public async void Dispatch()
        {
            Outcome<TResult, Exception> outcome;
            if (realProxy.proxyName != null)
                CallLogging.LogStart(realProxy.proxyName, method.Name, args);
            object obj;
            try
            {
                obj = method.Invoke(realProxy.target, args);
            }
            catch (TargetInvocationException e)
            {
                outcome = new Outcome<TResult, Exception>(e.InnerException);
                goto Finish;
            }
            Task<TResult> task = obj as Task<TResult>;
            if (task == null)
            {
                // Can only happen when we chose TResult == Void2 for a method
                // that actually returns plain Task.

                // The more obvious task = ((Task)obj).ContinueWith(_ => default(TResult))
                // executes outside the synchronization context and posts a continuation
                // back to the synchronization context, which calls Send, which trips my
                // assertion in the P# scheduler.  The P# task handling needs to change
                // anyway, but it's fine to keep this version.
                task = ((Func<Task<TResult>>)(async () =>
                {
                    await (Task)obj;
                    return default(TResult);
                }))();
            }
            // From here to Finish is basically
            // "Catching<Exception>.AwaitTaskAsync(task)", but such a method
            // would be prone to misuse and this is the only place we need it,
            // so inline it here.
            TResult result;
            try
            {
                result = await task;
            }
            catch (Exception ex)
            {
                outcome = new Outcome<TResult, Exception>(ex);
                goto Finish;
            }
            outcome = new Outcome<TResult, Exception>(result);
            Finish:
            if (realProxy.proxyName != null)
                CallLogging.LogEnd(realProxy.proxyName, method.Name, outcome, args);
            replyTarget.SetOutcome(outcome);
        }
    }

    public static class PSharpProxyStatics
    {
        public static TIface MakeTransparentProxy<TIface>(this Machine hostMachine, TIface target,
            string targetDebugName, MachineId callerMachineId, EventFactory eventFactory)
        {
            return PSharpRealProxy.MakeTransparentProxy(callerMachineId, hostMachine.Id, target, targetDebugName, eventFactory);
        }
    }
}
