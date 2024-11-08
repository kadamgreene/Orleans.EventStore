using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.EventSourcing.Storage
{
    public interface ISnapshotPolicy
    {
        bool ShouldTakeSnapshot(object state, int version, IEnumerable<object> events);
    }
}
