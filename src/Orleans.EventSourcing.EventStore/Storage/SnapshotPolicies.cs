namespace Orleans.EventSourcing.Storage
{
    public static class SnapshotPolicies
    {
        public static ISnapshotPolicy None => new NoSnapshotPolicy();
        public static ISnapshotPolicy Every(int events) => new IntervalSnapshotPolicy(events);

        private class IntervalSnapshotPolicy : ISnapshotPolicy
        {
            private int _interval;

            public IntervalSnapshotPolicy(int interval)
            {
                _interval = interval;
            }

            public bool ShouldTakeSnapshot(object state, int version, IEnumerable<object> events)
            {
                return version % _interval == 0;
            }
        }
    }
}