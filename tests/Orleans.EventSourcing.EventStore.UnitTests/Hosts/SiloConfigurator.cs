using EventStore.Client;
using Orleans.TestingHost;

namespace Orleans.EventSourcing.EventStore.UnitTests.Hosts;

public class SiloConfigurator : ISiloConfigurator
{
    /// <inheritdoc />
    public void Configure(ISiloBuilder silo)
    {
        var eventStoreConnectionString = "esdb://localhost:2113?tls=false";
        silo.AddEventStoreBasedLogConsistencyProvider(Constants.LogConsistencyStoreName,
                                                      options =>
                                                      {
                                                          options.ClientSettings = EventStoreClientSettings.Create(eventStoreConnectionString);
                                                      })
            .AddMemoryGrainStorage(Constants.LogSnapshotStoreName);
    }
}
