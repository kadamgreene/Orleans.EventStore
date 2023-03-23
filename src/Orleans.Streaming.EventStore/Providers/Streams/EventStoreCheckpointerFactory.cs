﻿using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Configuration.Overrides;
using Orleans.Streams;

namespace Orleans.Providers.Streams.EventStore;

/// <summary>
///     Factory for creating <see cref="EventStoreCheckpointer" /> instances.
/// </summary>
public class EventStoreCheckpointerFactory : IStreamQueueCheckpointerFactory
{
    private readonly string _providerName;
    private readonly EventStoreStreamCheckpointerOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ClusterOptions _clusterOptions;

    /// <summary>
    /// </summary>
    /// <param name="providerName"></param>
    /// <param name="options"></param>
    /// <param name="clusterOptions"></param>
    /// <param name="loggerFactory"></param>
    public EventStoreCheckpointerFactory(string providerName, EventStoreStreamCheckpointerOptions options, IOptions<ClusterOptions> clusterOptions, ILoggerFactory loggerFactory)
    {
        ArgumentException.ThrowIfNullOrEmpty(providerName, nameof(providerName));
        ArgumentNullException.ThrowIfNull(options, nameof(options));
        ArgumentNullException.ThrowIfNull(clusterOptions, nameof(clusterOptions));
        ArgumentNullException.ThrowIfNull(loggerFactory, nameof(loggerFactory));
        _providerName = providerName;
        _options = options;
        _clusterOptions = clusterOptions.Value;
        _loggerFactory = loggerFactory;
    }

    /// <summary>
    ///     Creates a stream checkpointer for the specified queue.
    /// </summary>
    /// <param name="queue">The queue name.</param>
    /// <returns>The stream checkpointer.</returns>
    public Task<IStreamQueueCheckpointer<string>> Create(string queue)
    {
        return Task.FromResult<IStreamQueueCheckpointer<string>>(EventStoreCheckpointer.Create(_clusterOptions.ServiceId, _providerName, queue, _options, _loggerFactory));
    }

    /// <summary>
    /// </summary>
    /// <param name="serviceProvider"></param>
    /// <param name="providerName"></param>
    /// <returns></returns>
    public static EventStoreCheckpointerFactory CreateFactory(IServiceProvider serviceProvider, string providerName)
    {
        var options = serviceProvider.GetOptionsByName<EventStoreStreamCheckpointerOptions>(providerName);
        var clusterOptions = serviceProvider.GetProviderClusterOptions(providerName);
        return ActivatorUtilities.CreateInstance<EventStoreCheckpointerFactory>(serviceProvider, providerName, options, clusterOptions);
    }
}
