// MigratingTable
// Copyright (c) Microsoft Corporation; see license.txt

using System;
using System.Threading.Tasks;

namespace ChainTableInterface
{
    // If you don't want to maintain two versions of code for void and non-void
    // operations, you can treat void operations as returning this type.  Pity
    // we can't use System.Void directly.
    //
    // To avoid gratuitous variation, the use of the Void2 parameterization of
    // an API that offers a separate "void" form is discouraged, except in code
    // that is itself responsible for bridging the "void" form to another API
    // that uses Void2.
    public struct Void2
    {
        public override bool Equals(object obj)
        {
            return obj is Void2;
        }
        public override int GetHashCode()
        {
            return 0;
        }
        public override string ToString()
        {
            return "void";
        }
    }

    // Default Outcome represents default TResult: reasonable.
    // I could define good Equals, GetHashCode, ToString here, but they
    // would have to call the "better" ones on the fields (or accept
    // implementations for the fields as parameters), which would be weird.
    public struct Outcome<TResult, TException> : IOutcome
        where TException : Exception
    {
        public TResult Result { get; }
        public TException Exception { get; }

        public Outcome(TResult result)
        {
            Result = result;
            Exception = null;
        }
        public Outcome(TException exception)
        {
            if (exception == null) throw new ArgumentNullException(nameof(exception));
            Result = default(TResult);
            Exception = exception;
        }

        object IOutcome.Result
        {
            get
            {
                return Result;
            }
        }

        Exception IOutcome.Exception
        {
            get
            {
                return Exception;
            }
        }
    }

    // Non-generic interface for introspection.  We could also use
    // WildcardCapturerBase, but this is less messy.
    public interface IOutcome
    {
        object Result { get; }
        Exception Exception { get; }
    }

    public static class Catching<TException> where TException : Exception
    {
        public static Outcome<TResult, TException> Run<TResult>(Func<TResult> func)
        {
            TResult result;
            try
            {
                result = func();
            }
            catch (TException ex)
            {
                return new Outcome<TResult, TException>(ex);
            }
            return new Outcome<TResult, TException>(result);
        }
        public static Outcome<Void2, TException> Run(Action action)
        {
            return Run(OutcomeStatics.ActionToFuncVoid2(action));
        }

        // We could have the caller call func and pass us the task, but some
        // "async API" methods (e.g., InMemoryTable) throw exceptions directly
        // (in which case the exception would escape our handling), unlike
        // compiler-translated async methods, which embed all exceptions in the
        // returned task.  I think it's more realistic to audit all consumers
        // that might treat direct exceptions differently from task exceptions
        // than to audit all producers of direct exceptions, but I could be
        // wrong.
        public static async Task<Outcome<TResult, TException>> RunAsync<TResult>(Func<Task<TResult>> funcAsync)
        {
            TResult result;
            try
            {
                result = await funcAsync();
            }
            catch (TException ex)
            {
                return new Outcome<TResult, TException>(ex);
            }
            return new Outcome<TResult, TException>(result);
        }
        public static Task<Outcome<Void2, TException>> RunAsync(Func<Task> funcAsync)
        {
            return RunAsync(OutcomeStatics.FuncTaskToFuncTaskVoid2(funcAsync));
        }
    }

    public static class OutcomeStatics
    {
        public static Func<Void2> ActionToFuncVoid2(Action action)
        {
            return () => { action(); return default(Void2); };
        }
        public static Func<Task<Void2>> FuncTaskToFuncTaskVoid2(Func<Task> funcAsync)
        {
            return async () =>
            {
                await funcAsync();
                return default(Void2);
            };
        }

        public static void SetOutcome<TResult, TException>(
            this TaskCompletionSource<TResult> tcs, Outcome<TResult, TException> outcome)
            where TException : Exception
        {
            if (outcome.Exception != null)
                tcs.SetException(outcome.Exception);
            else
                tcs.SetResult(outcome.Result);
        }

        public static TResult LogOutcome<TResult>(Func<TResult> func, Action<Outcome<TResult, Exception>> logger)
        {
            TResult result;
            try
            {
                result = func();
            }
            catch (Exception ex)
            {
                logger(new Outcome<TResult, Exception>(ex));
                throw;
            }
            logger(new Outcome<TResult, Exception>(result));
            return result;
        }

        public static void LogOutcome(Action action, Action<Outcome<Void2, Exception>> logger)
        {
            LogOutcome(ActionToFuncVoid2(action), logger);
        }

        // See comment on Catching.RunAsync re Func parameter.
        public static async Task<TResult> LogOutcomeAsync<TResult>(Func<Task<TResult>> funcAsync, Action<Outcome<TResult, Exception>> logger)
        {
            // This code looks like a duplicate of LogOutcome, but there's no
            // good way to factor it out without risking worse effects.
            TResult result;
            try
            {
                result = await funcAsync();
            }
            catch (Exception ex)
            {
                logger(new Outcome<TResult, Exception>(ex));
                throw;
            }
            logger(new Outcome<TResult, Exception>(result));
            return result;
        }

        public static async Task LogOutcomeAsync(Func<Task> funcAsync, Action<Outcome<Void2, Exception>> logger)
        {
            await LogOutcomeAsync(FuncTaskToFuncTaskVoid2(funcAsync), logger);
        }
    }
}
