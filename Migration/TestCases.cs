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
    class RandomCallServiceMachineCore : ServiceMachineCore
    {
        string NondeterministicUserPropertyFilterString()
        {
            switch (PSharpNondeterminism.Choice(3))
            {
                case 0: return "";
                case 1: return TableQuery.GenerateFilterConditionForBool("isHappy", QueryComparisons.Equal, false);
                case 2: return TableQuery.GenerateFilterConditionForBool("isHappy", QueryComparisons.Equal, true);
                default: throw new NotImplementedException();  // not reached
            }
        }

        async Task DoRandomAtomicCalls()
        {
            for (int callNum = 0; callNum < MigrationModel.NUM_CALLS_PER_MACHINE; callNum++)
            {
                SortedDictionary<PrimaryKey, DynamicTableEntity> dump = await peekProxy.DumpReferenceTableAsync();

                if (PSharpRuntime.Nondeterministic())
                {
                    // Query
                    var query = new TableQuery<DynamicTableEntity>();
                    query.FilterString = ChainTableUtils.CombineFilters(
                        TableQuery.GenerateFilterCondition(
                            TableConstants.PartitionKey, QueryComparisons.Equal, MigrationModel.SINGLE_PARTITION_KEY),
                        TableOperators.And,
                        NondeterministicUserPropertyFilterString());
                    await RunQueryAtomicAsync(query);
                }
                else
                {
                    // Batch write
                    int batchSize = PSharpRuntime.Nondeterministic() ? 2 : 1;
                    var batch = new TableBatchOperation();
                    var rowKeyChoices = new List<string> { "0", "1", "2", "3", "4", "5" };

                    for (int opNum = 0; opNum < batchSize; opNum++)
                    {
                        int opTypeNum = PSharpNondeterminism.Choice(7);
                        int rowKeyI = PSharpNondeterminism.Choice(rowKeyChoices.Count);
                        string rowKey = rowKeyChoices[rowKeyI];
                        rowKeyChoices.RemoveAt(rowKeyI);  // Avoid duplicate in same batch
                        var primaryKey = new PrimaryKey(MigrationModel.SINGLE_PARTITION_KEY, rowKey);
                        string eTag = null;
                        if (opTypeNum >= 1 && opTypeNum <= 3)
                        {
                            DynamicTableEntity existingEntity;
                            int etagTypeNum = PSharpNondeterminism.Choice(
                                dump.TryGetValue(primaryKey, out existingEntity) ? 3 : 2);
                            switch (etagTypeNum)
                            {
                                case 0: eTag = ChainTable2Constants.ETAG_ANY; break;
                                case 1: eTag = "wrong"; break;
                                case 2: eTag = existingEntity.ETag; break;
                            }
                        }
                        DynamicTableEntity entity = new DynamicTableEntity
                        {
                            PartitionKey = MigrationModel.SINGLE_PARTITION_KEY,
                            RowKey = rowKey,
                            ETag = eTag,
                            Properties = new Dictionary<string, EntityProperty> {
                                // Give us something to see on merge.  Might help with tracing too!
                                { string.Format("{0}_c{1}_o{2}", machineId.ToString(), callNum, opNum),
                                    new EntityProperty(true) },
                                // Property with 50%/50% distribution for use in filters.
                                { "isHappy", new EntityProperty(PSharpRuntime.Nondeterministic()) }
                            }
                        };
                        switch (opTypeNum)
                        {
                            case 0: batch.Insert(entity); break;
                            case 1: batch.Replace(entity); break;
                            case 2: batch.Merge(entity); break;
                            case 3: batch.Delete(entity); break;
                            case 4: batch.InsertOrReplace(entity); break;
                            case 5: batch.InsertOrMerge(entity); break;
                            case 6:
                                entity.ETag = ChainTable2Constants.ETAG_DELETE_IF_EXISTS;
                                batch.Delete(entity); break;
                        }
                    }

                    await RunBatchAsync(batch);
                }
            }
        }

        async Task DoRandomQueryStreamedAsync()
        {
            var query = new TableQuery<DynamicTableEntity>();
            query.FilterString = NondeterministicUserPropertyFilterString();
            await RunQueryStreamedAsync(query);
        }

        internal override Task Run()
        {
            if (PSharpRuntime.Nondeterministic())
            {
                return DoRandomQueryStreamedAsync();
            }
            else
            {
                return DoRandomAtomicCalls();
            }
        }
    }

    class Bug1ServiceMachineCore : ServiceMachineCore
    {
        internal override void InitializeHooks()
        {
            OnTableClientState(TableClientState.PREFER_NEW, async () => {
                TableBatchOperation batch = new TableBatchOperation();
                batch.InsertOrMerge(TestUtils.CreateTestEntity2("0", false));
                await RunBatchAsync(batch);

                await RunQueryStreamedAsync(new TableQuery<DynamicTableEntity>
                {
                    FilterString = TableQuery.GenerateFilterConditionForBool("isHappy", QueryComparisons.Equal, true)
                });
            });
        }

        internal override Task Run()
        {
            // Work is done in Bug1Subscriber so we can force it to happen at the right time.
            return Task.CompletedTask;
        }
    }

    class Bug9UpdateMachineCore : ServiceMachineCore
    {
        internal override void InitializeHooks()
        {
            OnTableClientState(TableClientState.PREFER_NEW, async () => {
                TableBatchOperation batch = new TableBatchOperation();
                batch.Insert(TestUtils.CreateTestEntity2("5", false));
                await RunBatchAsync(batch);
            });
        }
        internal override Task Run()
        {
            return Task.CompletedTask;
        }
    }

    class Bug9QueryMachineCore : ServiceMachineCore
    {
        internal override async Task Run()
        {
            var query = new TableQuery<DynamicTableEntity> {
                FilterString = TableQuery.GenerateFilterCondition(
                    TableConstants.PartitionKey, QueryComparisons.Equal, MigrationModel.SINGLE_PARTITION_KEY)
            };
            await RunQueryAtomicAsync(query);
        }
    }

    class Bug10DeleteMachineCore : ServiceMachineCore
    {
        internal override void InitializeHooks()
        {
            OnTableClientState(TableClientState.USE_NEW_HIDE_METADATA, async () => {
                TableBatchOperation batch = new TableBatchOperation();
                batch.Delete(new DynamicTableEntity {
                    PartitionKey = MigrationModel.SINGLE_PARTITION_KEY,
                    RowKey = "0",
                    ETag = ChainTable2Constants.ETAG_ANY
                });
                await RunBatchAsync(batch);
            });
        }
        internal override Task Run()
        {
            return Task.CompletedTask;
        }
    }

    class Bug10QueryMachineCore : ServiceMachineCore
    {
        internal override void InitializeHooks()
        {
            AfterTableClientState(TableClientState.PREFER_NEW, async () => {
                var query = new TableQuery<DynamicTableEntity>
                {
                    FilterString = TableQuery.GenerateFilterCondition(
                        TableConstants.PartitionKey, QueryComparisons.Equal, MigrationModel.SINGLE_PARTITION_KEY)
                };
                await RunQueryAtomicAsync(query);
            });
        }
        internal override Task Run()
        {
            return Task.CompletedTask;
        }
    }

    class Bug11ServiceMachineCore : ServiceMachineCore
    {
        internal override void InitializeHooks()
        {
            AfterTableClientState(TableClientState.PREFER_OLD, async () => {
                TableBatchOperation batch = new TableBatchOperation();
                batch.Insert(TestUtils.CreateTestEntity2("0", false));
                await RunBatchAsync(batch);
            });
            OnTableClientState(TableClientState.USE_NEW_WITH_TOMBSTONES, async () => {
                var query = new TableQuery<DynamicTableEntity>
                {
                    FilterString = TableQuery.GenerateFilterCondition(
                        TableConstants.PartitionKey, QueryComparisons.Equal, MigrationModel.SINGLE_PARTITION_KEY)
                };
                await RunQueryAtomicAsync(query);
            });
        }
        internal override Task Run()
        {
            return Task.CompletedTask;
        }
    }

    // P# compiler lets us get away with this for now.  If we run up against its
    // restrictions later, we can break out a TablesMachineCore.
    partial class TablesMachine
    {
        async Task LoadDefaultDataAsync()
        {
#if false
            MTableEntity eMeta = new MTableEntity {
                PartitionKey = MigrationModel.SINGLE_PARTITION_KEY,
                RowKey = MigratingTable.ROW_KEY_PARTITION_META,
                partitionState = MTablePartitionState.SWITCHED,
            };
            MTableEntity e0 = TestUtils.CreateTestMTableEntity("0", "orange");
            MTableEntity e1old = TestUtils.CreateTestMTableEntity("1", "red");
            MTableEntity e2new = TestUtils.CreateTestMTableEntity("2", "green");
            MTableEntity e3old = TestUtils.CreateTestMTableEntity("3", "blue");
            MTableEntity e3new = TestUtils.CreateTestMTableEntity("3", "azure");
            MTableEntity e4old = TestUtils.CreateTestMTableEntity("4", "yellow");
            MTableEntity e4new = TestUtils.CreateTestMTableEntity("4", null, true);
            var oldBatch = new TableBatchOperation();
            oldBatch.InsertOrReplace(eMeta);
            oldBatch.InsertOrReplace(e0);
            oldBatch.InsertOrReplace(e1old);
            oldBatch.InsertOrReplace(e3old);
            oldBatch.InsertOrReplace(e4old);
            IList<TableResult> oldTableResult = await oldTable.ExecuteBatchAsync(oldBatch);
            await ExecuteExportedMirrorBatchAsync(oldBatch, oldTableResult);
            var newBatch = new TableBatchOperation();
            newBatch.InsertOrReplace(e0);
            newBatch.InsertOrReplace(e2new);
            newBatch.InsertOrReplace(e3new);
            newBatch.InsertOrReplace(e4new);
            IList<TableResult> newTableResult = await newTable.ExecuteBatchAsync(newBatch);
            // Allow rows to overwrite rather than composing the virtual ETags manually.
            // InsertOrReplace doesn't use the ETag, so we don't care that the ETag was mutated by the original batch.
            await ExecuteExportedMirrorBatchAsync(newBatch, newTableResult);
#endif

            // Start with the old table now.
            var batch = new TableBatchOperation();
            batch.InsertOrReplace(TestUtils.CreateTestEntity2("0", true));
            batch.InsertOrReplace(TestUtils.CreateTestEntity2("1", false));
            batch.InsertOrReplace(TestUtils.CreateTestEntity2("3", false));
            batch.InsertOrReplace(TestUtils.CreateTestEntity2("4", true));
            IList<TableResult> oldTableResult = await oldTable.ExecuteBatchAsync(batch);
            // InsertOrReplace doesn't use the ETag, so we don't care that the ETag was mutated by the original batch.
            await referenceTable.ExecuteMirrorBatchAsync(batch, oldTableResult);
        }

        async Task InitializeTestCaseAsync()
        {
            string bugTest = MigrationModel.GetEnabledBugTest();
            switch (bugTest)
            {
                case null:
                    await LoadDefaultDataAsync();
                    for (int i = 0; i < 2; i++)
                        AddServiceMachineCore(new RandomCallServiceMachineCore());
                    return;
                case "1":
                    await LoadDefaultDataAsync();
                    AddServiceMachineCore(new Bug1ServiceMachineCore());
                    return;
                case "9":
                    await LoadDefaultDataAsync();
                    AddServiceMachineCore(new Bug9UpdateMachineCore());
                    AddServiceMachineCore(new Bug9QueryMachineCore());
                    return;
                case "10":
                    await LoadDefaultDataAsync();
                    AddServiceMachineCore(new Bug10DeleteMachineCore());
                    AddServiceMachineCore(new Bug10QueryMachineCore());
                    return;
                case "11":
                    // Important!  This test case starts with an empty partition.
                    AddServiceMachineCore(new Bug11ServiceMachineCore());
                    return;
                default:
                    throw new ArgumentException(string.Format("Unrecognized mtablebugtest: {0}", bugTest));
            }
        }
    }
}
