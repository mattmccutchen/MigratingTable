// MigratingTable
// Copyright (c) Microsoft Corporation; see license.txt

using Microsoft.WindowsAzure.Storage;
using ChainTableInterface;
using Microsoft.WindowsAzure.Storage.Table;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace ChainTableInterface
{
    /* Equality comparer that compares the content of some objects we care
     * about that don't override Equals (and GetHashCode) themselves.
     *
     * This is only guaranteed to be right for the objects we use.  While we
     * try to be targeted in the additional comparisons we define, it's hard
     * to rule out the existence of subclasses for which the comparisons here
     * might be inappropriate.
     *
     * In general, we compare collections without regard to their type
     * arguments, as in Java.  Ideally, one would use the fastest
     * EqualityComparer for the actual type arguments, which would require
     * some fancy type manipulation, but this isn't worth worrying about for
     * our purposes. */
    public class BetterComparer : EqualityComparer<object>
    {
        private BetterComparer() { }
        public static readonly BetterComparer Instance = new BetterComparer();

        public override bool Equals(object x, object y)
        {
            // If we had a lot of these cases, we could make some framework
            // to fixpoint a combination of EqualityComparers.
            // XXX: Now we do, but still not a priority.
            IOutcome xOutcome, yOutcome;
            if ((xOutcome = x as IOutcome) != null
                && (yOutcome = y as IOutcome) != null)
            {
                return Equals(xOutcome.Result, yOutcome.Result)
                    && Equals(xOutcome.Exception, yOutcome.Exception);
            }
            StorageException xSE, ySE;
            if ((xSE = x as StorageException) != null
                && (ySE = y as StorageException) != null)
            {
                // This is probably the first decision in BetterComparer that is
                // definitely not general-purpose.  This will have the effect of
                // comparing status codes and FailedOpIndex (if applicable) for
                // StorageExceptions that are part of the semantics.
                return Equals(xSE.Message, ySE.Message);
            }
            ITableEntity xEntity, yEntity;
            if ((xEntity = x as ITableEntity) != null
                && (yEntity = y as ITableEntity) != null)
            {
                // Some of these fields might not be considered significant
                // in all contexts.  We're placing the burden on callers to
                // make sure they are reproducible in order to use
                // BetterComparer.
                return xEntity.PartitionKey == yEntity.PartitionKey
                    && xEntity.RowKey == yEntity.RowKey
                    && xEntity.Timestamp == yEntity.Timestamp
                    && xEntity.ETag == yEntity.ETag
                    && Equals(xEntity.WriteEntity(null), yEntity.WriteEntity(null));
            }
            TableResult xResult, yResult;
            if ((xResult = x as TableResult) != null
                && (yResult = y as TableResult) != null)
            {
                return xResult.HttpStatusCode == yResult.HttpStatusCode
                    && xResult.Etag == yResult.Etag
                    && Equals(xResult.Result, yResult.Result);
            }
            /*
            If there were an easy way to generalize this to all type
            arguments, I might do it, but there doesn't seem to be.
            IReadOnlyDictionary<TKey,TValue> isn't covariant (I guess
            because it needs to support custom IEqualityComparers that only
            accept TKey?) and there's no non-generic IReadOnlyDictionary.
            */
            IReadOnlyDictionary<string, EntityProperty> xPropertyDict, yPropertyDict;
            if ((xPropertyDict = x as IReadOnlyDictionary<string, EntityProperty>) != null
                && (yPropertyDict = y as IReadOnlyDictionary<string, EntityProperty>) != null)
            {
                return DictEquals(xPropertyDict, yPropertyDict);
            }
            // IReadOnlyList is covariant, so a list of any type argument
            // will pass "as IReadOnlyList<object>".  Cool!
            IReadOnlyList<object> xList, yList;
            if ((xList = x as IReadOnlyList<object>) != null
                && (yList = y as IReadOnlyList<object>) != null)
            {
                return xList.SequenceEqual(yList, this);
            }
            return object.Equals(x, y);
        }

        public override int GetHashCode(object obj)
        {
            IOutcome outcome;
            if ((outcome = obj as IOutcome) != null)
                return Hasher.Start.With(GetHashCode(outcome.Result)).With(GetHashCode(outcome.Exception));
            ITableEntity entity;
            if ((entity = obj as ITableEntity) != null)
            {
                return Hasher.Start.With(entity.PartitionKey.GetHashCode())
                    .With(entity.RowKey.GetHashCode())
                    .With(entity.Timestamp.GetHashCode())
                    .With(entity.ETag.GetHashCode())
                    .With(GetHashCode(entity.WriteEntity(null)));
            }
            TableResult result;
            if ((result = obj as TableResult) != null)
            {
                return Hasher.Start.With(result.HttpStatusCode.GetHashCode())
                    .With(result.Etag.GetHashCode())
                    .With(GetHashCode(result.Result));
            }
            IReadOnlyDictionary<string, EntityProperty> propertyDict;
            if ((propertyDict = obj as IReadOnlyDictionary<string, EntityProperty>) != null)
            {
                return DictHashCode(propertyDict);
            }
            IReadOnlyList<object> list;
            if ((list = obj as IReadOnlyList<object>) != null)
            {
                return list.Aggregate(Hasher.Start, (h, e) => h.With(GetHashCode(e)));
            }
            // There's no static Object.GetHashCode(Object).
            if (obj == null) return 0;
            return obj.GetHashCode();
        }

        private bool DictEquals<TKey, TValue>(
            IReadOnlyDictionary<TKey, TValue> dictA,
            IReadOnlyDictionary<TKey, TValue> dictB)
        {
            return dictA.Count == dictB.Count &&
                dictA.All(kvpA =>
                {
                    TValue valueB;
                    return dictB.TryGetValue(kvpA.Key, out valueB) && Equals(kvpA.Value, valueB);
                });
        }
        private int DictHashCode<TKey, TValue>(IReadOnlyDictionary<TKey, TValue> dict)
        {
            unchecked
            {
                // I would probably support KeyValuePair as another case in
                // BetterComparer if it were easy, but it's nontrivial
                // to check for a KeyValuePair of any type arguments. :(
                return dict.Aggregate(0, (h, kvp) => h + Hasher.Start.With(GetHashCode(kvp.Key)).With(GetHashCode(kvp.Value)));
            }
        }

        class TableQueryToString : WildcardCapturerBase<string>
        {
            TableQueryToString() : base(typeof(TableQuery<>)) { }
            public string Invoke<TElement>(TableQuery<TElement> query)
            {
                return string.Format("TableQuery<{0}>{{FilterString={1}, SelectColumns={2}, TakeCount={3}}}",
                    query.ElementType, query.FilterString, query.SelectColumns, query.TakeCount);
            }
            internal static readonly TableQueryToString Instance = new TableQueryToString();
        }
        class KeyValuePairToString : WildcardCapturerBase<string>
        {
            KeyValuePairToString() : base(typeof(KeyValuePair<,>)) { }
            public string Invoke<TKey, TValue>(KeyValuePair<TKey, TValue> kvp)
            {
                // Nicer notation in general, and print null as "null".
                return string.Format("({0}: {1})", BetterComparer.ToString(kvp.Key), BetterComparer.ToString(kvp.Value));
            }
            internal static readonly KeyValuePairToString Instance = new KeyValuePairToString();
        }

        public static string ToString(object obj)
        {
            if (obj == null) return "null";
            string ret;
            IOutcome outcome;
            if ((outcome = obj as IOutcome) != null)
                return (outcome.Exception != null) ? ToString(outcome.Exception) : ToString(outcome.Result);
            TableOperation op;
            if ((op = obj as TableOperation) != null)
            {
                if (op.GetOperationType() == TableOperationType.Retrieve)
                    return "TableOperation.Retrieve(" + op.GetRetrievePartitionKey() + "," + op.GetRetrieveRowKey() + ")";
                else
                    return "TableOperation." + op.GetOperationType().ToString() + "(" + ToString(op.GetEntity()) + ")";
            }
            // TableBatchOperation does not implement IReadOnlyList<object>, and
            // we'd like to display the class anyway.
            TableBatchOperation batch;
            if ((batch = obj as TableBatchOperation) != null)
            {
                return "TableBatchOperation{" + string.Join(",", from e in batch select ToString(e)) + "}";
            }
            if ((ret = TableQueryToString.Instance.CaptureOrDefault(obj)) != null)
                return ret;
            if ((ret = KeyValuePairToString.Instance.CaptureOrDefault(obj)) != null)
                return ret;
            ITableEntity entity;
            if ((entity = obj as ITableEntity) != null)
            {
                var kvps = new List<KeyValuePair<string, object>> {
                    new KeyValuePair<string, object>("PartitionKey", entity.PartitionKey),
                    new KeyValuePair<string, object>("RowKey", entity.RowKey),
                    new KeyValuePair<string, object>("ETag", entity.ETag),
                    new KeyValuePair<string, object>("Timestamp", entity.Timestamp),
                };
                // Ordinal here is not semantics-critical but might gain us some reproducibility.
                kvps.AddRange(from kvp in entity.WriteEntity(null).OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
                              select new KeyValuePair<string, object>(kvp.Key, kvp.Value.PropertyAsObject));
                return "ITableEntity{" + string.Join(",", (from e in kvps select ToString(e))) + "}";
            }
            TableResult result;
            if ((result = obj as TableResult) != null)
            {
                return "TableResult{HttpStatusCode=" + result.HttpStatusCode + ", ETag=" + result.Etag + ", Result=" + ToString(result.Result) + "}";
            }
            IReadOnlyList<object> list;
            if ((list = obj as IReadOnlyList<object>) != null)
            {
                return "IReadOnlyList{" + string.Join(",", (from e in list select ToString(e))) + "}";
            }
            return obj.ToString();
        }
    }
}
