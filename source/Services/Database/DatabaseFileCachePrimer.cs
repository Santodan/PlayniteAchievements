using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace PlayniteAchievements.Services.Database
{
    /// <summary>
    /// Reads the database files through sequentially so Windows holds their pages in its file
    /// cache before a whole-library query runs. Such a query touches pages scattered across the
    /// file, and reading those one by one from disk took 16 s for a 79 MB database whose pages
    /// were no longer cached, against 0.5 s once they were.
    /// </summary>
    internal sealed class DatabaseFileCachePrimer
    {
        internal static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(5);

        // Under the 85,000 byte large object heap threshold.
        private const int BufferSize = 64 * 1024;

        private readonly ILogger _logger;
        private readonly IReadOnlyList<string> _paths;
        private readonly TimeSpan _interval;
        private readonly Func<TimeSpan> _clock;
        private readonly object _sync = new object();
        private TimeSpan? _lastPrimedAt;

        public DatabaseFileCachePrimer(
            ILogger logger,
            IEnumerable<string> paths,
            TimeSpan? interval = null,
            Func<TimeSpan> clock = null)
        {
            _logger = logger;
            _paths = (paths ?? Enumerable.Empty<string>())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToList();
            _interval = interval ?? DefaultInterval;
            if (clock == null)
            {
                var stopwatch = Stopwatch.StartNew();
                clock = () => stopwatch.Elapsed;
            }

            _clock = clock;
        }

        /// <summary>
        /// Reads the files through unless that was done within the interval. Returns the number
        /// of bytes read, or zero when the read was skipped. Callers arriving while a read is in
        /// progress wait for it and then skip. Never throws.
        /// </summary>
        public long PrimeIfDue()
        {
            lock (_sync)
            {
                var now = _clock();
                if (_lastPrimedAt.HasValue && now - _lastPrimedAt.Value < _interval)
                {
                    return 0;
                }

                var stopwatch = Stopwatch.StartNew();
                long bytes = 0;
                var buffer = new byte[BufferSize];
                foreach (var path in _paths)
                {
                    bytes += ReadThrough(path, buffer);
                }

                _lastPrimedAt = _clock();
                _logger?.Debug($"[FileCachePrime] ms={stopwatch.ElapsedMilliseconds} bytes={bytes}");
                return bytes;
            }
        }

        private long ReadThrough(string path, byte[] buffer)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return 0;
                }

                // Shared for write and delete: the database connections keep these files open,
                // and the write-ahead log is rewritten and removed underneath this read.
                using (var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    BufferSize,
                    FileOptions.SequentialScan))
                {
                    long total = 0;
                    int read;
                    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        total += read;
                    }

                    return total;
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"[FileCachePrime] Could not read '{path}'.");
                return 0;
            }
        }
    }
}
