using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Orleans.EventSourcing.Storage
{
    internal class SnapshotPolicyDriver : ISnapshotPolicy
    {
        private ISnapshotPolicy snapshotPolicy;

        public SnapshotPolicyDriver(IOptions<EventStoreStorageOptions> options)
        {
            snapshotPolicy = options.Value.SnapshotPolicy;
        }

        public bool ShouldTakeSnapshot(object state, int version, IEnumerable<object> events)
        {
            return snapshotPolicy != null ? snapshotPolicy.ShouldTakeSnapshot(state, version, events) : false;
        }
    }
}