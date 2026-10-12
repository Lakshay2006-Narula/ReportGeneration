using System.Collections.Concurrent;

namespace SignalTracker.Services
{
    public sealed class ReportProgressItem
    {
        public string JobId { get; init; } = "";
        public int Progress { get; set; }
        public string Stage { get; set; } = "";
        public int Status { get; set; }
        public bool IsCompleted { get; set; }
        public bool IsFailed { get; set; }
        public bool IsProcessing => !IsCompleted && !IsFailed;
        public string? Error { get; set; }
        public string? FileName { get; set; }
        public string? DownloadPath { get; set; }
        public byte[]? FileBytes { get; set; }
        public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    public static class ReportProgressTracker
    {
        private static readonly ConcurrentDictionary<string, ReportProgressItem> Items = new();

        public static ReportProgressItem? Get(string jobId)
        {
            CleanupExpired();
            return Items.TryGetValue(jobId, out var item) ? item : null;
        }

        public static void Update(string jobId, int progress, string stage, int status)
        {
            var item = Items.GetOrAdd(jobId, id => new ReportProgressItem { JobId = id });
            item.Progress = Math.Clamp(progress, 0, 100);
            item.Stage = stage;
            item.Status = status;
            item.UpdatedAt = DateTime.UtcNow;
        }

        public static void Complete(string jobId, string fileName, string? downloadPath = null, byte[]? bytes = null)
        {
            var item = Items.GetOrAdd(jobId, id => new ReportProgressItem { JobId = id });
            item.Progress = 100;
            item.Stage = "Report ready.";
            item.Status = 3;
            item.IsCompleted = true;
            item.IsFailed = false;
            item.Error = null;
            item.FileName = fileName;
            item.DownloadPath = downloadPath;
            item.FileBytes = bytes;
            item.UpdatedAt = DateTime.UtcNow;
        }

        public static void Fail(string jobId, string error)
        {
            var item = Items.GetOrAdd(jobId, id => new ReportProgressItem { JobId = id });
            item.Status = 4;
            item.IsCompleted = false;
            item.IsFailed = true;
            item.Error = error;
            item.Stage = "Report failed.";
            item.UpdatedAt = DateTime.UtcNow;
        }

        private static void CleanupExpired()
        {
            var cutoff = DateTime.UtcNow.AddHours(-2);
            foreach (var item in Items)
            {
                if (item.Value.UpdatedAt < cutoff)
                    Items.TryRemove(item.Key, out _);
            }
        }
    }
}
