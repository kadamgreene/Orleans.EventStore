using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.EventSourcing;
using Orleans.EventSourcing.EventStoreStorage;
using Orleans.EventSourcing.Storage;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Storage;

namespace Orleans.Hosting;

/// <summary>
/// </summary>
public static class EventStoreStorageServiceCollectionExtensions
{
    /// <summary>
    ///     Configures EventStore as the default log consistency storage provider.
    /// </summary>
    public static IServiceCollection AddEventStoreBasedLogConsistencyProviderAsDefault(this IServiceCollection services, Action<EventStoreStorageOptions> configureOptions)
    {
        return services.AddEventStoreBasedLogConsistencyProvider(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, ob => ob.Configure(configureOptions));
    }

    /// <summary>
    ///     Configures EventStore as a log consistency storage provider.
    /// </summary>
    public static IServiceCollection AddEventStoreBasedLogConsistencyProvider(this IServiceCollection services, string name, Action<EventStoreStorageOptions> configureOptions)
    {
        return services.AddEventStoreBasedLogConsistencyProvider(name, ob => ob.Configure(configureOptions));
    }

    /// <summary>
    ///     Configures EventStore as the default log consistency storage provider.
    /// </summary>
    public static IServiceCollection AddEventStoreBasedLogConsistencyProviderAsDefault(this IServiceCollection services, Action<OptionsBuilder<EventStoreStorageOptions>>? configureOptions = null)
    {
        return services.AddEventStoreBasedLogConsistencyProvider(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
    }

    /// <summary>
    ///     Configures EventStore as a log consistency storage provider.
    /// </summary>
    public static IServiceCollection AddEventStoreBasedLogConsistencyProvider(this IServiceCollection services, string name, Action<OptionsBuilder<EventStoreStorageOptions>>? configureOptions = null)
    {
        // Create config options
        configureOptions?.Invoke(services.AddOptions<EventStoreStorageOptions>(name));

        // Configure log consistency.
        services.TryAddSingleton<Factory<IGrainContext, ILogConsistencyProtocolServices>>(serviceProvider =>
        {
            var protocolServicesFactory = ActivatorUtilities.CreateFactory(typeof(DefaultProtocolServices), new[] { typeof(IGrainContext) });
            return grainContext => (ILogConsistencyProtocolServices)protocolServicesFactory(serviceProvider, new object[] { grainContext });
        });

        services.TryAddKeyedSingleton<ISnapshotPolicy, SnapshotPolicyDriver>(name);

        // Configure log storage.
        services.AddTransient<IConfigurationValidator>(sp => new EventStoreStorageOptionsValidator(sp.GetRequiredService<IOptionsMonitor<EventStoreStorageOptions>>().Get(name), name));
        services.AddTransient<IPostConfigureOptions<EventStoreStorageOptions>, DefaultStorageProviderSerializerOptionsConfigurator<EventStoreStorageOptions>>();
        services.ConfigureNamedOptionForLogging<EventStoreStorageOptions>(name);

        services.TryAddSingleton(sp => sp.GetKeyedService<ILogConsistentStorage>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME));
        services.AddKeyedSingleton<ILogConsistentStorage>(name, (sp, obj) => EventStoreLogConsistentStorageFactory.Create(sp, obj.ToString()));
        services.AddSingleton<ILifecycleParticipant<ISiloLifecycle>>((sp) => (ILifecycleParticipant<ISiloLifecycle>)sp.GetRequiredKeyedService<ILogConsistentStorage>(name));

        // Configure log view adaptor.
        services.TryAddSingleton<ILogViewAdaptorFactory>(sp => sp.GetKeyedService<ILogViewAdaptorFactory>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME));
        services.AddKeyedSingleton<ILogViewAdaptorFactory>(name, (sp, name) => LogConsistencyProviderFactory.Create(sp, name.ToString()));
        return services;
    }
}
