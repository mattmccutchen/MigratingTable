// MigratingTable
// Copyright (c) Microsoft Corporation; see license.txt

using System;
using System.Threading.Tasks;
using Microsoft.PSharp;
using System.Collections.Generic;
using Microsoft.WindowsAzure.Storage.Table;
using Microsoft.WindowsAzure.Storage.Table.Protocol;
using Microsoft.WindowsAzure.Storage;
using ChainTableInterface;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;

namespace Migration
{
    interface IMirrorChainTable2 : IChainTable2
    {
        /*
         * Like ExecuteBatch but sets the new ETags the same as in
         * originalResponse (assuming the mirror batch succeeds).  If
         * originalResponse is null, equivalent to ExecuteBatchAsync.
         *
         * originalBatch must be a copy of the original batch with the
         * _original_ ETag (i.e., If-Match) fields for correct processing.
         */
        Task<IList<TableResult>> ExecuteMirrorBatchAsync(
            TableBatchOperation originalBatch, IList<TableResult> originalResponse,
            TableRequestOptions requestOptions = null, OperationContext operationContext = null);
    }
    abstract class AbstractMirrorChainTable2 : AbstractChainTable2, IMirrorChainTable2
    {
        public abstract Task<IList<TableResult>> ExecuteMirrorBatchAsync(
            TableBatchOperation originalBatch, IList<TableResult> originalResponse,
            TableRequestOptions requestOptions = null, OperationContext operationContext = null);

        public override Task<IList<TableResult>> ExecuteBatchAsync(TableBatchOperation batch,
            TableRequestOptions requestOptions = null, OperationContext operationContext = null)
        {
            return ExecuteMirrorBatchAsync(batch, null, requestOptions, operationContext);
        }
    }

    // This is expected to be a single call to one of the ITable methods.
    delegate Task<object> TableCall(IChainTable2 table);
    delegate Task<object> MirrorTableCall(IMirrorChainTable2 table);

    static class MigrationModel
    {
        // Start with a single partition.
        internal static readonly string SINGLE_PARTITION_KEY = "";

        internal static readonly int NUM_CALLS_PER_MACHINE = 2;

        // If we go over this, we assume we're in an infinite loop.
        // Revise as necessary for test case size.
        internal static readonly int TABLE_CALL_LIMIT = 100;

        internal static Dictionary<string, string> TestVars = new Dictionary<string, string>();

        // Provide a similar API we can use even in P# production mode.
        internal static string GetTestVar(string name)
        {
            string value;
            if (TestVars.TryGetValue(name, out value))
                return value;
            else
                return PSharpRuntime.GetTestVar(name);
        }

        internal static MTableOptionalBug? GetEnabledBug()
        {
            string bugVar = GetTestVar("mtablebug");
            if (bugVar == null) return null;
            int i = int.Parse(bugVar);
            if (i >= 0 && i < (int)MTableOptionalBug.NumBugs)
                return (MTableOptionalBug)i;
            throw new ArgumentOutOfRangeException("mtablebug testvar");
        }
        internal static string GetEnabledBugTest()
        {
            return GetTestVar("mtablebugtest");
        }
    }

    interface ITablesMachinePeek
    {
        Task<SortedDictionary<PrimaryKey, DynamicTableEntity>> DumpReferenceTableAsync();

        // XXX: The following methods are here because they don't participate in
        // the alternation of table calls and annotations.  The "peek" in the
        // interface name has become a misnomer.

        Task<int> GetReferenceTableRevisionAsync();

        Task<List<DynamicTableEntity>> GetValidStreamReadRows(int startRevision, FilterExpression filterExpr, PrimaryKey continuationKey);
    }
    interface ITablesMachineAnnotation
    {
        /*
        referenceCall != null means the last backend call was the linearization
        point.  referenceCall is performed and its return value is returned.

        referenceCall == null means the last backend call was not the
        linearization point, and this method just returns null.

        Either way, the tables machine is unlocked for subsequent table calls.
        */
        Task<object> AnnotateLastBackendCallAsync(
            MirrorTableCall referenceCall,
            IList<SpuriousETagChange> spuriousETagChanges);
    }

    static class TestUtils
    {
        internal static DynamicTableEntity CreateTestEntity(string rowKey, string color)
        {
            return new DynamicTableEntity
            {
                PartitionKey = MigrationModel.SINGLE_PARTITION_KEY,
                RowKey = rowKey,
                ETag = ChainTable2Constants.ETAG_ANY,
                Properties = new Dictionary<string, EntityProperty>
                {
                    { "color", new EntityProperty(color) },
                },
            };
        }
        internal static DynamicTableEntity CreateTestEntity2(string rowKey, bool isHappy)
        {
            return new DynamicTableEntity
            {
                PartitionKey = MigrationModel.SINGLE_PARTITION_KEY,
                RowKey = rowKey,
                ETag = ChainTable2Constants.ETAG_ANY,
                Properties = new Dictionary<string, EntityProperty>
                {
                    { "isHappy", new EntityProperty(isHappy) },
                },
            };
        }
        internal static MTableEntity CreateTestMTableEntity(string rowKey, string color, bool deleted = false)
        {
            var ent = new MTableEntity
            {
                PartitionKey = MigrationModel.SINGLE_PARTITION_KEY,
                RowKey = rowKey,
                deleted = deleted,
            };
            if (color != null)
                ent.userProperties["color"] = new EntityProperty(color);
            return ent;
        }
    }

    /*
    Move to a separate class to work around P# restriction on nested classes in machines.
    We could defer creation of ServiceMachineCore until Initialize, but I want to just
    do the workaround and not make unrelated changes I might have to reverse later.

    Things we currently use from the machine:
    - Id: Easy enough to pass in constructor.
    - Payload: Easy enough to pass (and downcast!) in each method.
    If there get to be multiple delegates, consider defining an interface.  Hopefully the
    original restriction will be removed first anyway.
    */
    abstract class AppMachineCore
    {
        internal /*readonly*/ MachineId machineId;
        internal /*readonly*/ ITablesMachinePeek peekProxy;
        internal /*readonly*/ ITablesMachineAnnotation annotationProxy;

        internal virtual void Initialize(MachineId machineId, AppMachineInitializePayload payload)
        {
            this.machineId = machineId;
            peekProxy = payload.peekProxy;
            annotationProxy = payload.annotationProxy;
        }

        internal virtual Task HandleLinearizationPoint(IList<TableResult> successfulBatchResult)
        {
            throw new NotImplementedException();
        }

        internal class ChainTableMonitor : IChainTableMonitor
        {
            // No inner classes => more boilerplate. :(
            AppMachineCore machine;
            internal ChainTableMonitor(AppMachineCore machine)
            {
                this.machine = machine;
            }
            public async Task AnnotateLastBackendCallAsync(
                bool wasLinearizationPoint = false,
                IList<TableResult> successfulBatchResult = null,
                IList<SpuriousETagChange> spuriousETagChanges = null)
            {
                //Trace.TraceInformation("{0} calling AnnotateLastBackendCallAsync(wasLinearizationPoint: {1}, successfulBatchResult: {2}, spuriousETagChanges: {3})",
                //    PSharpRuntime.CurrentMachineId, wasLinearizationPoint, BetterComparer.ToString(successfulBatchResult), BetterComparer.ToString(spuriousETagChanges));
                if (wasLinearizationPoint)
                {
                    await machine.HandleLinearizationPoint(successfulBatchResult);
                }
                else
                {
                    await machine.annotationProxy.AnnotateLastBackendCallAsync(null, spuriousETagChanges);
                }
            }
        }

        internal abstract Task Run();
    }

    class AppMachineInitializeEvent : Event { }
    // This is a lot of boilerplate but appears to be the recommended approach
    // (https://msdn.microsoft.com/en-us/library/bb383979.aspx), modulo my
    // continuing resistance to adopting auto-implemented properties instead of fields.
    // Python's namedtuple (+ strong typing) would be the way to do this.
    class AppMachineInitializePayload
    {
        internal readonly IConfigurationService<MTableConfiguration> configService;
        internal readonly IChainTable2 oldTable;
        internal readonly IChainTable2 newTable;
        internal readonly ITablesMachinePeek peekProxy;
        internal readonly ITablesMachineAnnotation annotationProxy;
        internal AppMachineInitializePayload(
            IConfigurationService<MTableConfiguration> configService,
            IChainTable2 oldTable, IChainTable2 newTable,
            ITablesMachinePeek peekProxy, ITablesMachineAnnotation annotationProxy)
        {
            this.configService = configService;
            this.oldTable = oldTable;
            this.newTable = newTable;
            this.peekProxy = peekProxy;
            this.annotationProxy = annotationProxy;
        }
    }

    abstract class ServiceMachineCore : AppMachineCore
    {
        IReadOnlyConfigurationService<MTableConfiguration> configService;
        /*readonly*/ MigratingTable migratingTable;
        MirrorTableCall currentReferenceCall;
        internal IList<TableResult> successfulBatchResult;  // read by reference calls in subclasses
        Outcome<object, StorageException>? currentReferenceOutcome;

        internal override void Initialize(MachineId machineId, AppMachineInitializePayload payload)
        {
            base.Initialize(machineId, payload);
            configService = payload.configService;
            migratingTable = new MigratingTable(configService, payload.oldTable, payload.newTable,
                new ChainTableMonitor(this), MigrationModel.GetEnabledBug());
            InitializeHooks();
        }

        internal virtual void InitializeHooks() { }

        // This method does not log what the call is, since there's no way to
        // know what's inside the delegates.  Use RunBatchAsync or RunQueryAtomicAsync.
        async Task RunCallAsync(TableCall originalCall, MirrorTableCall referenceCall)
        {
            // TODO: All assertions should show what the call was.
            // XXX: We currently have no way to detect incorrect interleaving of
            // backend calls and AnnotateLastOutgoingCall here.  Most incorrect
            // interleavings will cause an error on the TablesMachine, but some
            // may go undetected.
            // - FIXME: A missing annotation will cause all machines to become blocked, and
            //   P# considers that a success!  To fix that, we need to enable liveness checking.

            currentReferenceCall = referenceCall;
            object actualOutcome = await Catching<StorageException>.RunAsync(() => originalCall(migratingTable));

            // Verify that successfulBatchResult was correct if specified.
            // (Ideally, we'd also catch if it isn't specified when it should
            // be, but that's less of a risk as it will likely cause ETag
            // mismatches anyway.)
            if (successfulBatchResult != null)
            {
                var successfulBatchOutcome = new Outcome<object, StorageException>(successfulBatchResult);
                PSharpRuntime.Assert(BetterComparer.Instance.Equals(successfulBatchOutcome, actualOutcome),
                    "{0} incorrect successfulBatchResult:\n{1}\nExpected:\n{2}\n", machineId,
                    BetterComparer.ToString(successfulBatchOutcome), BetterComparer.ToString(actualOutcome));
            }

            PSharpRuntime.Assert(currentReferenceOutcome != null,
                "{0}: The call completed without reporting a linearization point.", machineId);
            PSharpRuntime.Assert(BetterComparer.Instance.Equals(actualOutcome, currentReferenceOutcome),
                "{0} table call outcome is incorrect:\n{1}\nExpected:\n{2}\n", machineId,
                BetterComparer.ToString(actualOutcome), BetterComparer.ToString(currentReferenceOutcome));

            Console.WriteLine("{0} table call outcome is correct:\n{1}", machineId, BetterComparer.ToString(actualOutcome));

            // Reset fields
            currentReferenceCall = null;
            successfulBatchResult = null;
            currentReferenceOutcome = null;
        }

        internal async Task RunQueryAtomicAsync(TableQuery<DynamicTableEntity> query)
        {
            // async/await pair needed to upcast the return value to object.
            TableCall originalCall = async table => await table.ExecuteQueryAtomicAsync(query);
            MirrorTableCall referenceCall = async referenceTable => await referenceTable.ExecuteQueryAtomicAsync(query);
            Console.WriteLine("{0} starting atomic query: {1}", machineId, query);
            await RunCallAsync(originalCall, referenceCall);
        }

        internal async Task RunBatchAsync(TableBatchOperation batch)
        {
            TableBatchOperation batchCopy = ChainTableUtils.CopyBatch<DynamicTableEntity>(batch);
            TableCall originalCall = async table => await table.ExecuteBatchAsync(batch);
            MirrorTableCall referenceCall = async referenceTable => await referenceTable.ExecuteMirrorBatchAsync(batchCopy, successfulBatchResult);
            Console.WriteLine("{0} starting batch: {1}", machineId, BetterComparer.ToString(batch));
            await RunCallAsync(originalCall, referenceCall);
        }

        internal override async Task HandleLinearizationPoint(IList<TableResult> successfulBatchResult)
        {
            PSharpRuntime.Assert(currentReferenceOutcome == null,
                "{0}: The call already reported a linearization point.", machineId);
            this.successfulBatchResult = successfulBatchResult;
            currentReferenceOutcome = await Catching<StorageException>.RunAsync(
                () => annotationProxy.AnnotateLastBackendCallAsync(currentReferenceCall, null));
        }

        internal async Task RunQueryStreamedAsync(TableQuery<DynamicTableEntity> query)
        {
            int startRevision = await peekProxy.GetReferenceTableRevisionAsync();
            FilterExpression filterExpr = ChainTableUtils.ParseFilterString(query.FilterString);
            Console.WriteLine("{0} starting streaming query: {1}", machineId, query);
            using (IQueryStream<DynamicTableEntity> stream = await migratingTable.ExecuteQueryStreamedAsync(query))
            {
                PrimaryKey lastKey = ChainTableUtils.FirstValidPrimaryKey;
                for (;;)
                {
                    PrimaryKey returnedContinuationKey = await stream.GetContinuationPrimaryKeyAsync();
                    PSharpRuntime.Assert(returnedContinuationKey == null || returnedContinuationKey.CompareTo(lastKey) >= 0,
                        "{0}: query stream continuation key is {1}, expected >= {2}",
                        machineId, returnedContinuationKey, lastKey);

                    DynamicTableEntity row = await stream.ReadRowAsync();  // may be null, meaning end of stream
                    // Must be after ReadRowAsync, otherwise additional rows could become valid
                    // due to a mutation between GetValidStreamReadRows and ReadRowAsync and
                    // we would falsely report a bug if ReadRowAsync returns one of those rows.
                    List<DynamicTableEntity> validRows = await peekProxy.GetValidStreamReadRows(startRevision, filterExpr, lastKey);
                    // Three cheers for automatic use of covariance in overload resolution!
                    PSharpRuntime.Assert(validRows.Contains(row, BetterComparer.Instance),
                        "{0} query stream returned {1}, which is not one of the valid rows: {2}",
                        machineId, BetterComparer.ToString(row), BetterComparer.ToString(validRows));
                    Console.WriteLine("{0} query stream returned row {1}, which is valid", machineId, BetterComparer.ToString(row));

                    if (row == null)
                    {
                        // Any returnedContinuationKey (including null) is less or equal to a row of null.
                        break;
                    }
                    else
                    {
                        PSharpRuntime.Assert(returnedContinuationKey != null && returnedContinuationKey.CompareTo(row.GetPrimaryKey()) <= 0,
                            "{0}: query stream continuation key is {1}, expected <= {2}",
                            machineId, returnedContinuationKey, row.GetPrimaryKey());
                        lastKey = ChainTableUtils.NextValidPrimaryKeyAfter(row.GetPrimaryKey());
                    }
                }
            }
            Console.WriteLine("{0} finished streaming query", machineId);
        }

        class StateTriggeredActionSubscriber : IConfigurationSubscriber<MTableConfiguration>
        {
            readonly ServiceMachineCore outer;  // Unused now...
            readonly TableClientState triggerState;
            readonly Func<Task> asyncAction;
            internal StateTriggeredActionSubscriber(ServiceMachineCore outer, TableClientState triggerState, Func<Task> asyncAction)
            {
                this.outer = outer;
                this.triggerState = triggerState;
                this.asyncAction = asyncAction;
            }

            public async Task ApplyConfigurationAsync(MTableConfiguration newConfig)
            {
                if (newConfig.state == triggerState)
                {
                    await asyncAction();
                }
            }
        }

        // Not the greatest naming.  OnTableClientState blocks the configuration
        // push until asyncAction completes, while AfterTableClientState does not.

        internal void OnTableClientState(TableClientState triggerState, Func<Task> asyncAction)
        {
            MTableConfiguration dummy;
            configService.Subscribe(new StateTriggeredActionSubscriber(this, triggerState, asyncAction), out dummy);
        }

        class ActionDispatchable : IDispatchable
        {
            readonly Action action;
            internal ActionDispatchable(Action action)
            {
                this.action = action;
            }
            public void Dispatch()
            {
                action();
            }
        }
        internal void AfterTableClientState(TableClientState triggerState, Action action)
        {
            OnTableClientState(triggerState, () => {
                PSharpRuntime.SendEvent(machineId, new GenericDispatchableEvent(), new ActionDispatchable(action));
                return Task.CompletedTask;
            });
        }
    }

    class ServiceMachine : Machine
    {
        /*readonly*/ ServiceMachineCore core;
        readonly MachineSynchronizationContext synchronizationContext;
        public ServiceMachine() {
            synchronizationContext = new MachineSynchronizationContext(Id);
        }

        void DispatchPayload()
        {
            using (synchronizationContext.AsCurrent())
            {
                ((IDispatchable)Payload).Dispatch();
            }
        }

        [Start]
        [OnEntry(nameof(SaveInitialPayload))]
        [OnEventGotoState(typeof(AppMachineInitializeEvent), typeof(MainState), nameof(Initialize))]
        class WaitingForInitialization : MachineState { }

        [OnEntry(nameof(Start))]
        [OnEventDoAction(typeof(GenericDispatchableEvent), nameof(DispatchPayload))]
        class MainState : MachineState { }

        void SaveInitialPayload()
        {
            core = (ServiceMachineCore)Payload;
        }
        void Initialize()
        {
            //Monitor<RunningServiceMachinesMonitor>(new ServiceMachineCountChangeEvent(), 1);
            core.Initialize(Id, (AppMachineInitializePayload)Payload);
        }
        void Start()
        {
            using (synchronizationContext.AsCurrent())
            {
                Run();
            }
        }
        async void Run()  // intentional fire-and-forget
        {
            await core.Run();
            //Monitor<RunningServiceMachinesMonitor>(new ServiceMachineCountChangeEvent(), -1);
        }
    }

    class MigratorMachineCore : AppMachineCore
    {
        /*readonly*/ MasterMigratingTable migratingTable;
        internal override void Initialize(MachineId machineId, AppMachineInitializePayload payload)
        {
            base.Initialize(machineId, payload);
            migratingTable = new MasterMigratingTable(payload.configService, payload.oldTable, payload.newTable,
                new ChainTableMonitor(this), MigrationModel.GetEnabledBug());
        }

        internal override async Task Run()
        {
            await migratingTable.MigrateAsync();
            // TODO: Verify that new table and reference table are equal after
            // migration, or just rely on randomly generated queries to test
            // this?
        }
    }

    class MigratorMachine : Machine
    {
        readonly MigratorMachineCore core;
        readonly MachineSynchronizationContext synchronizationContext;
        public MigratorMachine()
        {
            core = new MigratorMachineCore();
            synchronizationContext = new MachineSynchronizationContext(Id);
        }

        void DispatchPayload()
        {
            using (synchronizationContext.AsCurrent())
            {
                ((IDispatchable)Payload).Dispatch();
            }
        }

        [Start]
        [OnEventGotoState(typeof(AppMachineInitializeEvent), typeof(MainState), nameof(Initialize))]
        class WaitingForInitialization : MachineState { }

        [OnEntry(nameof(Start))]
        [OnEventDoAction(typeof(GenericDispatchableEvent), nameof(DispatchPayload))]
        class MainState : MachineState { }

        void Initialize()
        {
            //Monitor<RunningServiceMachinesMonitor>(new ServiceMachineCountChangeEvent(), 1);
            core.Initialize(Id, (AppMachineInitializePayload)Payload);
        }
        void Start()
        {
            using (synchronizationContext.AsCurrent())
            {
                Run();
            }
        }
        async void Run()  // intentional fire-and-forget
        {
            await core.Run();
            //Monitor<RunningServiceMachinesMonitor>(new ServiceMachineCountChangeEvent(), -1);
        }
    }

    class ConfigurationServicePSharpProxy<TConfig> : IConfigurationService<TConfig>
    {
        readonly InMemoryConfigurationService<TConfig> mirror;
        readonly IConfigurationService<TConfig> originalProxy;

        class Subscriber : IConfigurationSubscriber<TConfig>
        {
            readonly ConfigurationServicePSharpProxy<TConfig> outer;
            internal Subscriber(ConfigurationServicePSharpProxy<TConfig> outer)
            {
                this.outer = outer;
            }
            public Task ApplyConfigurationAsync(TConfig newConfig)
            {
                return outer.mirror.PushConfigurationAsync(newConfig);
            }
        }

        internal ConfigurationServicePSharpProxy(MachineId callerMachineId, MachineId hostMachineId,
            IConfigurationService<TConfig> original)
        {
            TConfig initialConfig;
            originalProxy = PSharpRealProxy.MakeTransparentProxy(callerMachineId, hostMachineId, original,
                null, () => new GenericDispatchableEvent());

            IConfigurationSubscriber<TConfig> subscriberReverseProxy =
                PSharpRealProxy.MakeTransparentProxy(hostMachineId, callerMachineId,
                    (IConfigurationSubscriber<TConfig>)new Subscriber(this), null,
                    () => new GenericDispatchableEvent());
            // XXX Implement IDisposable.  Would need to proxy the dispose as well.
            original.Subscribe(subscriberReverseProxy, out initialConfig);

            mirror = new InMemoryConfigurationService<TConfig>(initialConfig);
        }
        public Task PushConfigurationAsync(TConfig newConfig)
        {
            return originalProxy.PushConfigurationAsync(newConfig);
        }

        public IDisposable Subscribe(IConfigurationSubscriber<TConfig> subscriber, out TConfig currentConfig)
        {
            return mirror.Subscribe(subscriber, out currentConfig);
        }
    }

    class ChainTable2PSharpProxy : AbstractChainTable2
    {
        readonly MachineId callerMachineId, hostMachineId;
        readonly IChainTable2 nonannotatableCallProxy;
        readonly IChainTable2 annotatableCallProxy;

        internal ChainTable2PSharpProxy(MachineId callerMachineId, MachineId hostMachineId, IChainTable2 original)
        {
            this.callerMachineId = callerMachineId;
            this.hostMachineId = hostMachineId;
            nonannotatableCallProxy = PSharpRealProxy.MakeTransparentProxy(callerMachineId, hostMachineId, original,
                null, () => new TableNonannotatableCallEvent());
            annotatableCallProxy = PSharpRealProxy.MakeTransparentProxy(callerMachineId, hostMachineId, original,
                null, () => new TableAnnotatableCallEvent());
        }

        public override Task<TableResult> ExecuteAsync(TableOperation operation, TableRequestOptions requestOptions = null, OperationContext operationContext = null)
        {
            return annotatableCallProxy.ExecuteAsync(operation, requestOptions, operationContext);
        }

        public override Task<IList<TableResult>> ExecuteBatchAsync(TableBatchOperation batch, TableRequestOptions requestOptions = null, OperationContext operationContext = null)
        {
            return annotatableCallProxy.ExecuteBatchAsync(batch, requestOptions, operationContext);
        }

        public override Task<IList<TElement>> ExecuteQueryAtomicAsync<TElement>(TableQuery<TElement> query, TableRequestOptions requestOptions = null, OperationContext operationContext = null)
        {
            return annotatableCallProxy.ExecuteQueryAtomicAsync(query, requestOptions, operationContext);
        }

        class QueryStreamPSharpProxy<TElement> : IQueryStream<TElement>
            where TElement : ITableEntity, new()
        {
            readonly IQueryStream<TElement> nonannotatableProxy;
            internal QueryStreamPSharpProxy(IQueryStream<TElement> nonannotatableProxy)
            {
                this.nonannotatableProxy = nonannotatableProxy;
            }

            // Dispose would take extra work to get working through
            // PSharpProxy (even if we would make it fire-and-forget since we
            // can't block locally, we need a way to call the void method on the
            // remote side), and we know the query streams that we proxy don't
            // have anything important in Dispose, so we can get away with this.
            public void Dispose() { }

            public Task<PrimaryKey> GetContinuationPrimaryKeyAsync()
            {
                return nonannotatableProxy.GetContinuationPrimaryKeyAsync();
            }

            public Task<TElement> ReadRowAsync()
            {
                return nonannotatableProxy.ReadRowAsync();
            }
        }

        public override async Task<IQueryStream<TElement>> ExecuteQueryStreamedAsync<TElement>(TableQuery<TElement> query, TableRequestOptions requestOptions = null, OperationContext operationContext = null)
        {
            // Philosophically, maybe this proxy-making belongs on the host
            // side, but that would require a second custom wrapper because we
            // still need the custom wrapper on the caller side to do the
            // different event types.
            IQueryStream<TElement> remoteStream = await nonannotatableCallProxy.ExecuteQueryStreamedAsync(
                query, requestOptions, operationContext);
            return new QueryStreamPSharpProxy<TElement>(
                PSharpRealProxy.MakeTransparentProxy(callerMachineId, hostMachineId, remoteStream,
                null, () => new TableNonannotatableCallEvent()));
        }
    }

    class TableNonannotatableCallEvent : Event { }
    class TableAnnotatableCallEvent : Event { }
    class TableCallAnnotationEvent : Event { }
    class TablesMachineInitializedEvent : Event { }

    class InMemoryTableWithHistory : AbstractMirrorChainTable2
    {
        InMemoryTable table = new InMemoryTable();
        internal List<SortedDictionary<PrimaryKey, DynamicTableEntity>> dumps;

        internal InMemoryTableWithHistory()
        {
            dumps = new List<SortedDictionary<PrimaryKey, DynamicTableEntity>> { table.Dump() };
        }

        internal int CurrentRevision { get { return dumps.Count - 1; } }

        public override Task<IList<TableResult>> ExecuteMirrorBatchAsync(TableBatchOperation originalBatch, IList<TableResult> originalResponse, TableRequestOptions requestOptions = null, OperationContext operationContext = null)
        {
            Task<IList<TableResult>> ret = table.ExecuteMirrorBatchAsync(originalBatch, originalResponse, requestOptions, operationContext);
            // Only on a successful write.  Writes that throw an exception do
            // not change the table state and accordingly bypass this.
            dumps.Add(table.Dump());
            return ret;
        }

        public override Task<IList<TElement>> ExecuteQueryAtomicAsync<TElement>(TableQuery<TElement> query, TableRequestOptions requestOptions = null, OperationContext operationContext = null)
        {
            return table.ExecuteQueryAtomicAsync(query, requestOptions, operationContext);
        }

        internal List<DynamicTableEntity> GetValidStreamReadRows(int startRevision, FilterExpression filterExpr, PrimaryKey continuationKey)
        {
            IEnumerable<IEnumerable<DynamicTableEntity>> filteredDumps =
                from dump in dumps.Skip(startRevision)
                select dump.Values.Where(row =>
                    (row.GetPrimaryKey().CompareTo(continuationKey) >= 0) && filterExpr.Evaluate(row));
            PrimaryKey nextMandatoryKey = (from dump in filteredDumps select dump.Select(row => row.GetPrimaryKey())).IntersectAll().FirstOrDefault();
            List<DynamicTableEntity> possible = filteredDumps.SelectMany(dump => dump.Where(row =>
                (nextMandatoryKey == null || row.GetPrimaryKey().CompareTo(nextMandatoryKey) <= 0)))
                .Distinct((IEqualityComparer<DynamicTableEntity>)BetterComparer.Instance).ToList();
            if (nextMandatoryKey == null)
                possible.Add(null);  // i.e., "end of stream" is a valid read
            return possible;
        }

        class NondeterministicQueryStream<TElement> : IQueryStream<TElement>
            where TElement : ITableEntity, new()
        {
            readonly InMemoryTableWithHistory outer;
            readonly int startRevision;
            readonly FilterExpression filterExpr;
            PrimaryKey continuationKey;

            internal NondeterministicQueryStream(InMemoryTableWithHistory outer, int startRevision, FilterExpression filterExpr)
            {
                this.outer = outer;
                this.startRevision = startRevision;
                this.filterExpr = filterExpr;
                continuationKey = ChainTableUtils.FirstValidPrimaryKey;
            }

            public void Dispose() { }

            public Task<PrimaryKey> GetContinuationPrimaryKeyAsync()
            {
                // MigratingTable currently doesn't use this, so no need to choose a probability distribution for it.
                throw new NotImplementedException();
            }

            public Task<TElement> ReadRowAsync()
            {
                if (continuationKey == null)
                    return Task.FromResult(default(TElement));
                List<DynamicTableEntity> possibleRows = outer.GetValidStreamReadRows(startRevision, filterExpr, continuationKey);
                int choiceIndex = PSharpNondeterminism.Choice(possibleRows.Count);
                DynamicTableEntity choice = possibleRows[choiceIndex];
                Console.WriteLine("NondeterministicQueryStream: possibleRows {0}, choiceIndex {1}",
                    BetterComparer.ToString(possibleRows), choiceIndex);
                continuationKey = (choice == null) ? null : ChainTableUtils.NextValidPrimaryKeyAfter(choice.GetPrimaryKey());
                return Task.FromResult((choice == null) ? default(TElement) : ChainTableUtils.CopyEntity<TElement>(choice));
            }
        }

        public override Task<IQueryStream<TElement>> ExecuteQueryStreamedAsync<TElement>(TableQuery<TElement> query, TableRequestOptions requestOptions = null, OperationContext operationContext = null)
        {
            FilterExpression filterExpr = ChainTableUtils.ParseFilterString(query.FilterString);
            if (query.SelectColumns != null)
                throw new NotImplementedException("select");
            if (query.TakeCount != null)
                throw new NotImplementedException("top");
            return Task.FromResult((IQueryStream<TElement>)
                new NondeterministicQueryStream<TElement>(this, CurrentRevision, filterExpr));
        }
    }

    partial class TablesMachine : Machine, ITablesMachinePeek, ITablesMachineAnnotation
    {
        readonly MachineSynchronizationContext synchronizationContext;
        public TablesMachine()
        {
            synchronizationContext = new MachineSynchronizationContext(Id);
        }

        InMemoryConfigurationService<MTableConfiguration> configService;
        InMemoryTableWithHistory oldTable, newTable;
        InMemoryTableWithHistory referenceTable;
        int numTableCalls = 0;

        [Start]
        [OnEntry(nameof(StartInitialization))]
        [OnEventGotoState(typeof(TablesMachineInitializedEvent), typeof(Ready))]
        [DeferEvents(typeof(TableNonannotatableCallEvent), typeof(TableAnnotatableCallEvent))]
        // XXX: Use Push or something to avoid having to redeclare GenericDispatchableEvent in every state?
        [OnEventDoAction(typeof(GenericDispatchableEvent), nameof(DispatchPayload))]
        class Initializing : MachineState { }

        [OnEventDoAction(typeof(TableNonannotatableCallEvent), nameof(DispatchPayload))]
        [OnEventGotoState(typeof(TableAnnotatableCallEvent), typeof(WaitingForAnnotation), nameof(DispatchTableCall))]
        [OnEventDoAction(typeof(GenericDispatchableEvent), nameof(DispatchPayload))]
        class Ready : MachineState { }

        [DeferEvents(typeof(TableNonannotatableCallEvent), typeof(TableAnnotatableCallEvent))]
        [OnEventGotoState(typeof(TableCallAnnotationEvent), typeof(Ready), nameof(DispatchPayload))]
        [OnEventDoAction(typeof(GenericDispatchableEvent), nameof(DispatchPayload))]
        class WaitingForAnnotation : MachineState { }

        void StartInitialization()
        {
            using (synchronizationContext.AsCurrent())
            {
                Initialize();
            }
        }

        Task<IList<TableResult>> ExecuteExportedMirrorBatchAsync(TableBatchOperation batch, IList<TableResult> originalResponse)
        {
            var exportedBatch = new TableBatchOperation();
            var exportedOriginalResponse = new List<TableResult>();
            for (int i = 0; i < batch.Count; i++)
            {
                TableOperation op = batch[i];
                var mtableEntity = (MTableEntity)op.GetEntity();
                if (MigratingTable.RowKeyIsInternal(mtableEntity.RowKey))
                    continue;
                exportedOriginalResponse.Add(originalResponse[i]);
                Debug.Assert(op.GetOperationType() == TableOperationType.InsertOrReplace);
                DynamicTableEntity exported = mtableEntity.Export<DynamicTableEntity>();
                if (mtableEntity.deleted)
                {
                    exported.ETag = ChainTable2Constants.ETAG_DELETE_IF_EXISTS;
                    exportedBatch.Delete(exported);
                }
                else
                {
                    exported.ETag = null;
                    exportedBatch.InsertOrReplace(exported);
                }
            }
            return referenceTable.ExecuteMirrorBatchAsync(exportedBatch, exportedOriginalResponse);
        }

        void InitializeAppMachine(MachineId appMachineId)
        {
            Send(appMachineId, new AppMachineInitializeEvent(), new AppMachineInitializePayload(
                new ConfigurationServicePSharpProxy<MTableConfiguration>(appMachineId, Id,
                    configService /*new ConfigurationServiceLoggingProxy<MTableConfiguration>(configService, string.Format("{0} configService", appMachineId))*/),
                new ChainTable2PSharpProxy(appMachineId, Id, new ChainTable2LoggingProxy(oldTable, string.Format("{0} oldTable", appMachineId))),
                new ChainTable2PSharpProxy(appMachineId, Id, new ChainTable2LoggingProxy(newTable, string.Format("{0} newTable", appMachineId))),
                this.MakeTransparentProxy((ITablesMachinePeek)this, "tablesMachinePeek",
                    appMachineId, () => new TableNonannotatableCallEvent()),
                this.MakeTransparentProxy((ITablesMachineAnnotation)this, "tablesMachineAnnotation",
                    appMachineId, () => new TableCallAnnotationEvent())));
        }

        async void Initialize()
        {
            configService = new InMemoryConfigurationService<MTableConfiguration>(
                MasterMigratingTable.INITIAL_CONFIGURATION);
            oldTable = new InMemoryTableWithHistory();
            newTable = new InMemoryTableWithHistory();
            referenceTable = new InMemoryTableWithHistory();

            //CreateMonitor(typeof(RunningServiceMachinesMonitor));

            await InitializeTestCaseAsync();

            InitializeAppMachine(CreateMachine(typeof(MigratorMachine)));

            Send(Id, new TablesMachineInitializedEvent());
        }

        void AddServiceMachineCore(ServiceMachineCore core)
        {
            InitializeAppMachine(CreateMachine(typeof(ServiceMachine), core));
        }

        void DispatchTableCall()
        {
            numTableCalls++;
            // Crude liveness check, since a monitor did not work (see below).
            PSharpRuntime.Assert(numTableCalls <= MigrationModel.TABLE_CALL_LIMIT,
                "A service machine may be in an infinite loop.");
            DispatchPayload();
        }

        void DispatchPayload()
        {
            using (synchronizationContext.AsCurrent())
            {
                ((IDispatchable)Payload).Dispatch();
            }
        }

        async Task<object> ITablesMachineAnnotation.AnnotateLastBackendCallAsync(
            MirrorTableCall referenceCall, IList<SpuriousETagChange> spuriousETagChanges)
        {
            if (spuriousETagChanges == null)
                spuriousETagChanges = new List<SpuriousETagChange>();

            if (referenceCall == null)
            {
                if (spuriousETagChanges.Count > 0)
                {
                    var batch = new TableBatchOperation();
                    var originalResponse = new List<TableResult>();
                    foreach (SpuriousETagChange change in spuriousETagChanges)
                    {
                        batch.Merge(new DynamicTableEntity
                        {
                            PartitionKey = change.partitionKey,
                            RowKey = change.rowKey,
                            ETag = ChainTable2Constants.ETAG_ANY
                        });
                        originalResponse.Add(new TableResult { Etag = change.newETag });
                    }
                    try
                    {
                        await referenceTable.ExecuteMirrorBatchAsync(batch, originalResponse, null, null);
                    }
                    catch (StorageException ex)
                    {
                        // Make sure this doesn't get swallowed by a generic StorageException catch block.
                        throw new InvalidOperationException("Invalid spurious ETag change annotation.", ex);
                    }
                }
                return null;
            }
            else
            {
                if (spuriousETagChanges.Count > 0)
                    throw new ArgumentException("spuriousETagChanges currently not allowed with a reference call");
                return await referenceCall(referenceTable);
            }
        }

        Task<SortedDictionary<PrimaryKey, DynamicTableEntity>> ITablesMachinePeek.DumpReferenceTableAsync()
        {
            return Task.FromResult(referenceTable.dumps.Last());
        }

        Task<int> ITablesMachinePeek.GetReferenceTableRevisionAsync() {
            return Task.FromResult(referenceTable.CurrentRevision);
        }

        Task<List<DynamicTableEntity>> ITablesMachinePeek.GetValidStreamReadRows(
            int startRevision, FilterExpression filterExpr, PrimaryKey continuationKey)
        {
            return Task.FromResult(referenceTable.GetValidStreamReadRows(startRevision, filterExpr, continuationKey));
        }
    }

#if false
    // Enabling P# liveness checking immediately caused a false positive because
    // the state caching does not consider the data in the table or the progress
    // of the ServiceMachines through their async methods.  Rather than try to
    // fix that, just switch to the table call limit for now.

    // Update: /noCaching may solve the problem.

    // The number of running service machines has changed.  Payload: change as an int.
    class ServiceMachineCountChangeEvent : Event { }
    class ServiceMachinesSomeEvent : Event { }
    class ServiceMachinesNoneEvent : Event { }

    class RunningServiceMachinesMonitor : Monitor
    {
        int numRunning = 0;

        [Start]
        [OnEventDoAction(typeof(ServiceMachineCountChangeEvent), nameof(ProcessCountChange))]
        [OnEventGotoState(typeof(ServiceMachinesNoneEvent), typeof(None))]
        [OnEventGotoState(typeof(ServiceMachinesSomeEvent), typeof(Some))]
        class None : MonitorState { }

        [Hot]
        [OnEventDoAction(typeof(ServiceMachineCountChangeEvent), nameof(ProcessCountChange))]
        [OnEventGotoState(typeof(ServiceMachinesNoneEvent), typeof(None))]
        [OnEventGotoState(typeof(ServiceMachinesSomeEvent), typeof(Some))]
        class Some : MonitorState { }

        void ProcessCountChange()
        {
            numRunning += (int)Payload;
            if (numRunning == 0)
                Raise(new ServiceMachinesNoneEvent());
            else
                Raise(new ServiceMachinesSomeEvent());
        }
    }
#endif

    static class Program
    {
        [Test]
        public static void PSharpEntryPoint()
        {
            PSharpRuntime.CreateMachine(typeof(TablesMachine));
        }
        public static void Main(string[] args)
        {
            foreach (string arg in args)
            {
                // Copied from PSharp CommandLineOptions.
                if (arg.ToLower().StartsWith("/testvar:") && arg.Contains("="))
                {
                    string[] parts = arg.Substring(9).Split(new char[] { '=' }, 2);
                    MigrationModel.TestVars[parts[0]] = parts[1];
                }
            }
            PSharpEntryPoint();
            Console.ReadLine();
        }
    }
}
