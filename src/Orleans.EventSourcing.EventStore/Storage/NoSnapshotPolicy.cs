namespace Orleans.EventSourcing.Storage
{
    internal class NoSnapshotPolicy : ISnapshotPolicy
    {
        public NoSnapshotPolicy()
        {
        }

        public bool ShouldTakeSnapshot(object state, int version, IEnumerable<object> events)
        {
            return false;
        }
    }
}
