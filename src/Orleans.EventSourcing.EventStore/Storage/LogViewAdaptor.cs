﻿using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Orleans.EventSourcing.Common;
using Orleans.EventSourcing.Storage;
using Orleans.Serialization;
using Orleans.Storage;

namespace Orleans.EventSourcing.EventStoreStorage;

/// <summary>
///     A log view adaptor that wraps around a traditional storage adaptor, and uses batching and e-tags
///     to append entries.
///     <para>
///         The log itself is actually saved to storage - the latest view (snapshot) and metadata (the log position, and write flags)
///         and all log entries are stored in the primary.
///     </para>
/// </summary>
/// <typeparam name="TLogView">Type of log view</typeparam>
/// <typeparam name="TLogEntry">Type of log entry</typeparam>
internal class LogViewAdaptor<TLogView, TLogEntry> : PrimaryBasedLogViewAdaptor<TLogView, TLogEntry, SubmissionEntry<TLogEntry>>
    where TLogView : class, new()
    where TLogEntry : class
{
    private readonly IGrainStorage _grainStorage;
    private readonly string _grainTypeName;
    private readonly ILogConsistentStorage _logStorage;
    private readonly DeepCopier _deepCopier;
    private readonly ISnapshotPolicy _snapshotPolicy;
    private SnapshotWithMetaDataAndETag<TLogView> _globalSnapshot = new();
    private TLogView _confirmedView = new();
    private int _confirmedVersion;
    private int _globalVersion;

    /// <summary>
    ///     Initializes a new instance of LogViewAdaptor class
    /// </summary>
    public LogViewAdaptor(ILogViewAdaptorHost<TLogView, TLogEntry> host, TLogView initialState, IGrainStorage grainStorage, string grainTypeName, ILogConsistencyProtocolServices services, ILogConsistentStorage logStorage, DeepCopier deepCopier, ISnapshotPolicy snapshotPolicy)
        : base(host, initialState, services)
    {
        ArgumentNullException.ThrowIfNull(grainStorage);
        ArgumentException.ThrowIfNullOrEmpty(grainTypeName);
        ArgumentNullException.ThrowIfNull(logStorage);
        _grainStorage = grainStorage;
        _grainTypeName = grainTypeName;
        _logStorage = logStorage;
        _deepCopier = deepCopier;
        _snapshotPolicy = snapshotPolicy;
    }

    /// <inheritdoc />
    protected override TLogView LastConfirmedView()
    {
        return _confirmedView;
    }

    /// <inheritdoc />
    protected override int GetConfirmedVersion()
    {
        return _confirmedVersion;
    }

    /// <inheritdoc />
    protected override void InitializeConfirmedView(TLogView initialState)
    {
        _globalSnapshot = new SnapshotWithMetaDataAndETag<TLogView>(initialState);
        _confirmedView = initialState;
        _confirmedVersion = 0;
        _globalVersion = 0;
    }

    private void UpdateConfirmedView(IReadOnlyList<TLogEntry> logEntries)
    {
        foreach (var logEntry in logEntries)
        {
            try
            {
                Host.UpdateView(_confirmedView, logEntry);
            }
            catch (Exception ex)
            {
                Services.CaughtUserCodeException("UpdateView", nameof(UpdateConfirmedView), ex);
            }
        }
    }

    /// <inheritdoc />
    public override Task<IReadOnlyList<TLogEntry>> RetrieveLogSegment(int fromVersion, int toVersion)
    {
        return _logStorage.ReadAsync<TLogEntry>(_grainTypeName, Services.GrainId, fromVersion, toVersion - fromVersion + 1);
    }

    /// <inheritdoc />
    protected override SubmissionEntry<TLogEntry> MakeSubmissionEntry(TLogEntry entry)
    {
        return new SubmissionEntry<TLogEntry> { Entry = entry };
    }

    #region Read & Write

    /// <inheritdoc />
    protected override async Task ReadAsync()
    {
        enter_operation("ReadAsync");
        while (true)
        {
            try
            {
                var snapshot = new SnapshotWithMetaDataAndETag<TLogView>();
                await _grainStorage.ReadStateAsync(_grainTypeName, Services.GrainId, snapshot);
                _globalSnapshot = snapshot;
                Services.Log(LogLevel.Debug, "read success {0}", _globalSnapshot);
                if (_confirmedVersion < _globalSnapshot.State.SnapshotVersion)
                {
                    _confirmedVersion = _globalSnapshot.State.SnapshotVersion;
                    _confirmedView = _deepCopier.Copy(_globalSnapshot.State.Snapshot);
                }
                try
                {
                    _globalVersion = await _logStorage.GetLastVersionAsync(_grainTypeName, Services.GrainId);
                    if (_confirmedVersion < _globalVersion)
                    {
                        var logEntries = await RetrieveLogSegment(_confirmedVersion + 1, _globalVersion);
                        Services.Log(LogLevel.Debug, "read success {0}", logEntries);
                        UpdateConfirmedView(logEntries);
                        _confirmedVersion = _globalVersion;
                    }
                    LastPrimaryIssue.Resolve(Host, Services);
                    break; // successful
                }
                catch (Exception ex)
                {
                    LastPrimaryIssue.Record(new ReadFromLogStorageFailed { Exception = ex }, Host, Services);
                }
            }
            catch (Exception ex)
            {
                LastPrimaryIssue.Record(new ReadFromSnapshotStorageFailed { Exception = ex }, Host, Services);
            }
            Services.Log(LogLevel.Debug, "read failed {0}", LastPrimaryIssue);
            await LastPrimaryIssue.DelayBeforeRetry();
        }
        exit_operation("ReadAsync");
    }

    /// <inheritdoc />
    protected override async Task<int> WriteAsync()
    {
        enter_operation("WriteAsync");
        var updates = GetCurrentBatchOfUpdates();
        var logsSuccessfullyAppended = false;
        var batchSuccessfullyWritten = false;
        var writebit = _globalSnapshot.State.FlipBit(Services.MyClusterId);
        var events = updates.Select(x => x.Entry).ToImmutableList();
        try
        {
            _globalVersion = await _logStorage.AppendAsync(_grainTypeName, Services.GrainId, events, _globalVersion);
            logsSuccessfullyAppended = true;
            Services.Log(LogLevel.Debug, "write success {0}", events);
            UpdateConfirmedView(events);
            _confirmedVersion = _globalVersion;
        }
        catch (Exception ex)
        {
            LastPrimaryIssue.Record(new UpdateLogStorageFailed { Exception = ex }, Host, Services);
        }
        if (logsSuccessfullyAppended)
        {
            try
            {
                var updatedState = base.TentativeView;
                if (_snapshotPolicy.ShouldTakeSnapshot(updatedState, _globalVersion, events.Cast<object>()))
                {
                    _globalSnapshot.State.Snapshot = updatedState;
                    _globalSnapshot.State.SnapshotVersion = _globalVersion;
                    await _grainStorage.WriteStateAsync(_grainTypeName, Services.GrainId, _globalSnapshot);
                    Services.Log(LogLevel.Debug, "write ({0} updates) success {1}", updates.Length, _globalSnapshot);
                }
                batchSuccessfullyWritten = true;
                LastPrimaryIssue.Resolve(Host, Services);
            }
            catch (Exception ex)
            {
                LastPrimaryIssue.Record(new UpdateSnapshotStorageFailed { Exception = ex }, Host, Services);
            }
        }
        if (!batchSuccessfullyWritten)
        {
            Services.Log(LogLevel.Debug, "write apparently failed {0}", LastPrimaryIssue);
            while (true) // be stubborn until we can read what is there
            {
                await LastPrimaryIssue.DelayBeforeRetry();
                try
                {
                    var snapshot = new SnapshotWithMetaDataAndETag<TLogView>();
                    await _grainStorage.ReadStateAsync(_grainTypeName, Services.GrainId, snapshot);
                    _globalSnapshot = snapshot;
                    Services.Log(LogLevel.Debug, "read success {0}", _globalSnapshot);
                    if (_confirmedVersion < _globalSnapshot.State.SnapshotVersion)
                    {
                        _confirmedVersion = _globalSnapshot.State.SnapshotVersion;
                        _confirmedView = _deepCopier.Copy(_globalSnapshot.State.Snapshot);
                    }
                    try
                    {
                        _globalVersion = await _logStorage.GetLastVersionAsync(_grainTypeName, Services.GrainId);
                        if (_confirmedVersion < _globalVersion)
                        {
                            var logEntries = await RetrieveLogSegment(_confirmedVersion + 1, _globalVersion);
                            Services.Log(LogLevel.Debug, "read success {0}", logEntries);
                            UpdateConfirmedView(logEntries);
                            _confirmedVersion = _globalVersion;
                        }
                        LastPrimaryIssue.Resolve(Host, Services);
                        break; // successful
                    }
                    catch (Exception ex)
                    {
                        LastPrimaryIssue.Record(new ReadFromLogStorageFailed { Exception = ex }, Host, Services);
                    }
                }
                catch (Exception ex)
                {
                    LastPrimaryIssue.Record(new ReadFromSnapshotStorageFailed { Exception = ex }, Host, Services);
                }
                Services.Log(LogLevel.Debug, "read failed {0}", LastPrimaryIssue);
            }
            // check if last apparently failed write was in fact successful
            if (writebit == _globalSnapshot.State.GetBit(Services.MyClusterId))
            {
                Services.Log(LogLevel.Debug, "last write ({0} updates) was actually a success {1}", updates.Length, _globalSnapshot);
                batchSuccessfullyWritten = true;
            }
        }
        exit_operation("WriteAsync");
        return batchSuccessfullyWritten ? updates.Length : 0;
    }

    #endregion

    #region Notifications

    private const int maxEntriesInNotifications = 200;

    /// <inheritdoc />
    protected override INotificationMessage Merge(INotificationMessage earlierMessage, INotificationMessage laterMessage)
    {
        if (earlierMessage is UpdateNotificationMessage earlier
         && laterMessage is UpdateNotificationMessage later
         && earlier.Origin == later.Origin
         && earlier.Version + later.Updates.Count == later.Version
         && earlier.Updates.Count + later.Updates.Count < maxEntriesInNotifications)
        {
            return new UpdateNotificationMessage
                   {
                       Version = later.Version,
                       Origin = later.Origin,
                       Updates = earlier.Updates.Concat(later.Updates).ToList(),
                       ETag = later.ETag
                   };
        }
        return base.Merge(earlierMessage, laterMessage); // keep only the version number
    }

    private readonly SortedList<long, UpdateNotificationMessage> _notifications = new();

    /// <inheritdoc />
    protected override void OnNotificationReceived(INotificationMessage payload)
    {
        if (payload is UpdateNotificationMessage um && um != null)
        {
            _notifications.Add(um.Version - um.Updates.Count, um);
        }
        else
        {
            base.OnNotificationReceived(payload);
        }
    }

    /// <inheritdoc />
    protected override void ProcessNotifications()
    {
        // discard notifications that are behind our already confirmed state
        while (_notifications.Count > 0 && _notifications.ElementAt(0).Key < _globalVersion)
        {
            Services.Log(LogLevel.Debug, "discarding notification {0}", _notifications.ElementAt(0).Value);
            _notifications.RemoveAt(0);
        }
        // process notifications that reflect next global version
        while (_notifications.Count > 0 && _notifications.ElementAt(0).Key == _globalVersion)
        {
            var updateNotification = _notifications.ElementAt(0).Value;
            _notifications.RemoveAt(0);
            _globalSnapshot.State.FlipBit(updateNotification.Origin);
            _globalSnapshot.ETag = updateNotification.ETag;
            _globalVersion = updateNotification.Version;
            UpdateConfirmedView(updateNotification.Updates);
            _confirmedVersion = updateNotification.Version;
            Services.Log(LogLevel.Debug, "notification success ({0} updates) {1}", updateNotification.Updates.Count, _globalSnapshot);
        }
        Services.Log(LogLevel.Trace, "unprocessed notifications in queue: {0}", _notifications.Count);
        base.ProcessNotifications();
    }

    #endregion

    #region Operation Failed Classes

    /// <summary>
    ///     Describes a connection issue that occurred when reading from the primary storage.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public sealed class ReadFromSnapshotStorageFailed : PrimaryOperationFailed
    {
        /// <inheritdoc />
        public override string ToString()
        {
            return $"read state from snapshot storage failed: caught {Exception.GetType().Name}: {Exception.Message}";
        }
    }

    /// <summary>
    ///     Describes a connection issue that occurred when updating the primary storage.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public sealed class UpdateSnapshotStorageFailed : PrimaryOperationFailed
    {
        /// <inheritdoc />
        public override string ToString()
        {
            return $"write state to snapshot storage failed: caught {Exception.GetType().Name}: {Exception.Message}";
        }
    }

    /// <summary>
    ///     Describes a connection issue that occurred when reading from the primary storage.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public sealed class ReadFromLogStorageFailed : PrimaryOperationFailed
    {
        /// <inheritdoc />
        public override string ToString()
        {
            return $"read logs from storage failed: caught {Exception.GetType().Name}: {Exception.Message}";
        }
    }

    /// <summary>
    ///     Describes a connection issue that occurred when updating the primary storage.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public sealed class UpdateLogStorageFailed : PrimaryOperationFailed
    {
        /// <inheritdoc />
        public override string ToString()
        {
            return $"write logs to storage failed: caught {Exception.GetType().Name}: {Exception.Message}";
        }
    }

    /// <summary>
    ///     A notification message sent to remote instances after updating this grain in storage.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    protected internal sealed class UpdateNotificationMessage : INotificationMessage
    {
        /// <inheritdoc />
        [Id(0)]
        public int Version { get; set; }

        /// <summary> The cluster that performed the update </summary>
        [Id(1)]
        public string Origin { get; set; } = null!;

        /// <summary> The list of updates that were applied </summary>
        [Id(2)]
        public List<TLogEntry> Updates { get; set; } = null!;

        /// <summary> The e-tag of the storage after applying the updates</summary>
        [Id(3)]
        public string ETag { get; set; } = null!;

        /// <inheritdoc />
        public override string ToString()
        {
            return $"v{Version} ({Updates.Count} updates by {Origin}) etag={ETag}";
        }
    }

    #endregion

    #region Debug

#if DEBUG
    private bool operation_in_progress;
#endif

    [Conditional("DEBUG")]
    private void enter_operation(string name)
    {
    #if DEBUG
        Services.Log(LogLevel.Trace, "/-- enter {0}", name);
        Debug.Assert(!operation_in_progress);
        operation_in_progress = true;
    #endif
    }

    [Conditional("DEBUG")]
    private void exit_operation(string name)
    {
    #if DEBUG
        Services.Log(LogLevel.Trace, "\\-- exit {0}", name);
        Debug.Assert(operation_in_progress);
        operation_in_progress = false;
    #endif
    }

    #endregion

}
