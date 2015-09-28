// MigratingTable
// Copyright (c) Microsoft Corporation; see license.txt

using Microsoft.WindowsAzure.Storage.Table;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Migration
{
    public class SpuriousETagChange
    {
        public readonly string partitionKey;
        public readonly string rowKey;
        public readonly string newETag;
        public SpuriousETagChange(string partitionKey, string rowKey, string newETag)
        {
            this.partitionKey = partitionKey;
            this.rowKey = rowKey;
            this.newETag = newETag;
        }
        public override string ToString()
        {
            return string.Format("SpuriousETagChange{{partitionKey={0}, rowKey={1}, newETag={2}}}",
                partitionKey, rowKey, newETag);
        }
    }

    public interface IChainTableMonitor
    {
        /*
         * The backend and monitor calls by verifiable table layers should follow this sequence
         * (this does not preclude other local work from being done in parallel with the calls):
         *
         *  (
         *    await backend call
         *    await AnnotateLastOutgoingCallAsync
         *  )*
         *
         * This applies even if the backend call throws an exception that is
         * part of the semantics, but verifiable table layers are allowed to
         * simply propagate unexpected exceptions since any such exceptions that
         * occur during testing reflect a bug in either the code under test or
         * the test harness and will be reported as such.
         *
         * Exception: Neither ExecuteQueryStreamed nor ReadRowAsync on the
         * resulting stream should be annotated.  Since neither of them can be
         * a linearization point or cause spurious ETag changes, the harness
         * does not expect an annotation for them.
         *
         * XXX: Have the verification framework enforce this automatically?
         */

        /*
         * Each call to ExecuteQueryAtomicAsync or ExecuteBatchAsync must report
         * exactly one backend call as its linearization point, even if it is
         * failing with a state-dependent exception.  Currently our test does
         * not cover invalid parameters that can be rejected without any backend
         * calls; if we add them, we'll need to find a way to annotate them.
         *
         * When reporting the linearization point for an ExecuteBatchAsync that
         * is going to succeed, then successfulBatchResult must be its result,
         * including the new ETags.  This allows the test harness to mirror the
         * batch on the reference table using the same new ETags before
         * unlocking the TablesMachine for other ServiceMachines.  (The test
         * does not use ExecuteAsync.)
         *
         * In all other cases, successfulBatchResult must be null.
         *
         * Passing one or more spurious ETag changes in combination with
         * wasLinearizationPoint = true is disallowed until we have a use case
         * to decide what its semantics should be.
         */
        Task AnnotateLastBackendCallAsync(
            bool wasLinearizationPoint = false,
            IList<TableResult> successfulBatchResult = null,
            IList<SpuriousETagChange> spuriousETagChanges = null);
    }
}
