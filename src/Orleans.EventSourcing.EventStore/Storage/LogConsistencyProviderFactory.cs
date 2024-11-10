using Microsoft.Extensions.DependencyInjection;
using Orleans.EventSourcing.Storage;
using Orleans.Providers;

namespace Orleans.EventSourcing.EventStoreStorage;

/// <summary>
///     Factory used to create instances of log consistent provider.
/// </summary>
public static class LogConsistencyProviderFactory
{
    /// <summary>
    ///     Creates a EventStore log consistent storage instance.
    /// </summary>
    public static LogConsistencyProvider Create(IServiceProvider serviceProvider, string name)
    {
        var logConsistentStorage = serviceProvider.GetRequiredKeyedService<ILogConsistentStorage>(name);
        var snapshotPolicy = serviceProvider.GetKeyedService<ISnapshotPolicy>(name);
        if (snapshotPolicy == null)
        {
            snapshotPolicy = serviceProvider.GetKeyedService<ISnapshotPolicy>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME);
        }
        return ActivatorUtilities.CreateInstance<LogConsistencyProvider>(serviceProvider, logConsistentStorage, snapshotPolicy);
    }
}
