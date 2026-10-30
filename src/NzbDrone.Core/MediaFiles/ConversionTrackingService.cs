using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NzbDrone.Core.Books;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Queue;

namespace NzbDrone.Core.MediaFiles
{
    public interface IConversionTrackingService
    {
        void Start(string downloadId, int targetQualityId, string targetQualityName, string message = null);
        void Progress(string downloadId, decimal? progress, string message = null);
        void RegisterCancellation(string downloadId, CancellationTokenSource cancellationTokenSource);
        bool Cancel(string downloadId);
        void Cancelled(string downloadId, string message = null);
        void Complete(string downloadId);
        void Fail(string downloadId, string errorMessage);
        void Clear(string downloadId);
        ConversionQueueStatus Get(string downloadId);
        void RegisterUntracked(string downloadId, UntrackedConversionContext context);
        List<UntrackedConversion> GetUntracked();
    }

    public class ConversionTrackingService : IConversionTrackingService
    {
        private static readonly TimeSpan TerminalStatusRetention = TimeSpan.FromDays(1);
        private static readonly TimeSpan PruneInterval = TimeSpan.FromMinutes(15);
        private static readonly ConcurrentDictionary<string, ConversionQueueStatus> _statuses = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellations = new(StringComparer.OrdinalIgnoreCase);

        // Conversions started by an import that has no download-client item (folder scans, manual
        // imports) have no TrackedDownload, so QueueService cannot derive a row for them. The
        // context recorded here is what lets the queue synthesise one. It is kept separate from
        // _statuses because Start and Fail replace the status object wholesale.
        private static readonly ConcurrentDictionary<string, UntrackedConversionContext> _untracked = new(StringComparer.OrdinalIgnoreCase);
        private static DateTime _lastPruneUtc = DateTime.MinValue;
        private readonly IEventAggregator _eventAggregator;

        public ConversionTrackingService(IEventAggregator eventAggregator)
        {
            _eventAggregator = eventAggregator;
        }

        public void Start(string downloadId, int targetQualityId, string targetQualityName, string message = null)
        {
            if (string.IsNullOrWhiteSpace(downloadId))
            {
                return;
            }

            PruneExpiredTerminalStatuses();

            _statuses[downloadId] = new ConversionQueueStatus
            {
                Status = "converting",
                TargetQualityId = targetQualityId,
                TargetQualityName = targetQualityName,
                Progress = null,
                Message = message,
                CanCancel = false,
                Updated = DateTime.UtcNow
            };

            PublishQueueUpdate();
        }

        public void Progress(string downloadId, decimal? progress, string message = null)
        {
            if (string.IsNullOrWhiteSpace(downloadId) || !_statuses.TryGetValue(downloadId, out var status))
            {
                return;
            }

            if (string.Equals(status.Status, "failed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status.Status, "cancelling", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status.Status, "cancelled", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (progress.HasValue)
            {
                progress = Math.Min(100m, Math.Max(0m, progress.Value));
            }

            status.Progress = progress;
            status.Message = message ?? status.Message;
            status.Updated = DateTime.UtcNow;

            PublishQueueUpdate();
        }

        public void RegisterCancellation(string downloadId, CancellationTokenSource cancellationTokenSource)
        {
            if (string.IsNullOrWhiteSpace(downloadId) || cancellationTokenSource == null)
            {
                return;
            }

            // Borrowed, never owned: the caller creates this source and disposes it itself
            // (ConversionJobService in its conversion finally, ImportApprovedBooks via `using`).
            // Disposing it here killed a source its owner still held and still cancels through.
            _cancellations[downloadId] = cancellationTokenSource;

            if (_statuses.TryGetValue(downloadId, out var status))
            {
                status.CanCancel = true;
                status.Updated = DateTime.UtcNow;
                PublishQueueUpdate();
            }
        }

        public bool Cancel(string downloadId)
        {
            if (string.IsNullOrWhiteSpace(downloadId) || !_cancellations.TryGetValue(downloadId, out var cancellationTokenSource))
            {
                return false;
            }

            cancellationTokenSource.Cancel();

            _statuses.AddOrUpdate(
                downloadId,
                _ => new ConversionQueueStatus
                {
                    Status = "cancelling",
                    Message = "Cancelling conversion",
                    CanCancel = false,
                    Updated = DateTime.UtcNow
                },
                (_, status) =>
                {
                    status.Status = "cancelling";
                    status.Message = "Cancelling conversion";
                    status.CanCancel = false;
                    status.Updated = DateTime.UtcNow;
                    return status;
                });

            PublishQueueUpdate();
            return true;
        }

        public void Cancelled(string downloadId, string message = null)
        {
            if (string.IsNullOrWhiteSpace(downloadId))
            {
                return;
            }

            PruneExpiredTerminalStatuses();

            _statuses.AddOrUpdate(
                downloadId,
                _ => new ConversionQueueStatus
                {
                    Status = "cancelled",
                    Progress = null,
                    Message = string.IsNullOrWhiteSpace(message) ? "Conversion cancelled" : message,
                    CanCancel = false,
                    Updated = DateTime.UtcNow
                },
                (_, status) =>
                {
                    status.Status = "cancelled";
                    status.Progress = null;
                    status.Message = string.IsNullOrWhiteSpace(message) ? "Conversion cancelled" : message;
                    status.CanCancel = false;
                    status.Updated = DateTime.UtcNow;
                    return status;
                });

            ClearCancellation(downloadId);
            PublishQueueUpdate();
        }

        public void Complete(string downloadId)
        {
            if (string.IsNullOrWhiteSpace(downloadId))
            {
                return;
            }

            _statuses.TryRemove(downloadId, out _);
            _untracked.TryRemove(downloadId, out _);
            ClearCancellation(downloadId);
            PublishQueueUpdate();
        }

        public void Fail(string downloadId, string errorMessage)
        {
            if (string.IsNullOrWhiteSpace(downloadId))
            {
                return;
            }

            PruneExpiredTerminalStatuses();

            _statuses[downloadId] = new ConversionQueueStatus
            {
                Status = "failed",
                Progress = null,
                Message = errorMessage,
                Updated = DateTime.UtcNow
            };

            ClearCancellation(downloadId);
            PublishQueueUpdate();
        }

        public void Clear(string downloadId)
        {
            if (string.IsNullOrWhiteSpace(downloadId))
            {
                return;
            }

            _statuses.TryRemove(downloadId, out _);
            _untracked.TryRemove(downloadId, out _);
            ClearCancellation(downloadId);
            PublishQueueUpdate();
        }

        public ConversionQueueStatus Get(string downloadId)
        {
            if (string.IsNullOrWhiteSpace(downloadId))
            {
                return null;
            }

            PruneExpiredTerminalStatuses();

            return _statuses.TryGetValue(downloadId, out var status) ? status : null;
        }

        public void RegisterUntracked(string downloadId, UntrackedConversionContext context)
        {
            if (string.IsNullOrWhiteSpace(downloadId) || context == null)
            {
                return;
            }

            _untracked[downloadId] = context;
        }

        public List<UntrackedConversion> GetUntracked()
        {
            PruneExpiredTerminalStatuses();

            return _untracked
                .Select(item => new
                {
                    item.Key,
                    item.Value,
                    Status = _statuses.TryGetValue(item.Key, out var status) ? status : null
                })
                .Where(item => item.Status != null)
                .Select(item => new UntrackedConversion
                {
                    DownloadId = item.Key,
                    Context = item.Value,
                    Status = item.Status
                })
                .ToList();
        }

        private static void ClearCancellation(string downloadId)
        {
            // Stop tracking it, but leave disposal to the owner. A terminal status here can land
            // while the owner is still unwinding and still holds the source: ConversionJobService
            // removes the conversion from _activeConversions only in its finally, so disposing
            // here made ApplicationShutdownRequested cancel an already-disposed source.
            _cancellations.TryRemove(downloadId, out _);
        }

        private void PublishQueueUpdate()
        {
            _eventAggregator.PublishEvent(new QueueUpdatedEvent());
        }

        private static void PruneExpiredTerminalStatuses()
        {
            var now = DateTime.UtcNow;
            if (now - _lastPruneUtc < PruneInterval)
            {
                return;
            }

            _lastPruneUtc = now;
            foreach (var item in _statuses)
            {
                var status = item.Value;
                if (status == null ||
                    !IsTerminalStatus(status.Status) ||
                    now - status.Updated <= TerminalStatusRetention)
                {
                    continue;
                }

                _statuses.TryRemove(item.Key, out _);
                _untracked.TryRemove(item.Key, out _);
            }
        }

        private static bool IsTerminalStatus(string status)
        {
            return string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase);
        }
    }

    public class ConversionQueueStatus
    {
        public string Status { get; set; }
        public int? TargetQualityId { get; set; }
        public string TargetQualityName { get; set; }
        public decimal? Progress { get; set; }
        public string Message { get; set; }
        public bool CanCancel { get; set; }
        public DateTime Updated { get; set; }
    }

    /// <summary>
    /// The queue-row detail a conversion with no download-client item cannot get from a TrackedDownload.
    /// </summary>
    public class UntrackedConversionContext
    {
        public Author Author { get; set; }
        public Book Book { get; set; }
        public string Title { get; set; }
        public QualityModel Quality { get; set; }
        public decimal Size { get; set; }
        public DateTime Added { get; set; }
    }

    public class UntrackedConversion
    {
        public string DownloadId { get; set; }
        public UntrackedConversionContext Context { get; set; }
        public ConversionQueueStatus Status { get; set; }
    }
}
