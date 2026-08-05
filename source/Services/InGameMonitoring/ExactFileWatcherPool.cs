using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace PlayniteAchievements.Services.InGameMonitoring
{
    /// <summary>
    /// Pools non-recursive FileSystemWatchers by directory and routes notifications only to
    /// subscribers for the exact normalized file path. Watcher failures are surfaced and the
    /// directory watcher rearms itself with bounded backoff.
    /// </summary>
    internal sealed class ExactFileWatcherPool : IDisposable
    {
        private sealed class Subscription : IDisposable
        {
            private ExactFileWatcherPool _owner;
            private readonly string _directory;
            private readonly string _path;
            private readonly Action<string, bool> _callback;

            public Subscription(
                ExactFileWatcherPool owner,
                string directory,
                string path,
                Action<string, bool> callback)
            {
                _owner = owner;
                _directory = directory;
                _path = path;
                _callback = callback;
            }

            public void Dispose()
            {
                var owner = Interlocked.Exchange(ref _owner, null);
                owner?.Unsubscribe(_directory, _path, _callback);
            }
        }

        private sealed class DirectoryWatch : IDisposable
        {
            private static readonly int[] RearmDelaysMs = { 1000, 2000, 5000, 10000 };

            private readonly object _sync = new object();
            private readonly string _directory;
            private readonly ILogger _logger;
            private readonly Dictionary<string, List<Action<string, bool>>> _callbacks =
                new Dictionary<string, List<Action<string, bool>>>(StringComparer.OrdinalIgnoreCase);

            private FileSystemWatcher _watcher;
            private Timer _rearmTimer;
            private int _rearmAttempt;
            private bool _disposed;

            public DirectoryWatch(string directory, ILogger logger)
            {
                _directory = directory;
                _logger = logger;
                CreateWatcher();
            }

            public bool IsEmpty
            {
                get
                {
                    lock (_sync)
                    {
                        return _callbacks.Count == 0;
                    }
                }
            }

            public void Add(string path, Action<string, bool> callback)
            {
                lock (_sync)
                {
                    if (!_callbacks.TryGetValue(path, out var callbacks))
                    {
                        callbacks = new List<Action<string, bool>>();
                        _callbacks[path] = callbacks;
                    }

                    callbacks.Add(callback);
                }
            }

            public void Remove(string path, Action<string, bool> callback)
            {
                lock (_sync)
                {
                    if (!_callbacks.TryGetValue(path, out var callbacks))
                    {
                        return;
                    }

                    callbacks.Remove(callback);
                    if (callbacks.Count == 0)
                    {
                        _callbacks.Remove(path);
                    }
                }
            }

            private void CreateWatcher()
            {
                lock (_sync)
                {
                    if (_disposed || _watcher != null || !Directory.Exists(_directory))
                    {
                        return;
                    }

                    try
                    {
                        var watcher = new FileSystemWatcher(_directory)
                        {
                            IncludeSubdirectories = false,
                            Filter = "*",
                            NotifyFilter =
                                NotifyFilters.FileName |
                                NotifyFilters.LastWrite |
                                NotifyFilters.Size |
                                NotifyFilters.CreationTime,
                            InternalBufferSize = 16 * 1024
                        };
                        watcher.Changed += OnChanged;
                        watcher.Created += OnChanged;
                        watcher.Deleted += OnChanged;
                        watcher.Renamed += OnRenamed;
                        watcher.Error += OnError;
                        watcher.EnableRaisingEvents = true;
                        _watcher = watcher;
                        _rearmAttempt = 0;
                    }
                    catch (Exception ex)
                    {
                        _logger?.Debug(ex, $"[InGameMonitor] Could not watch '{_directory}'.");
                        ScheduleRearmLocked();
                    }
                }
            }

            private void OnChanged(object sender, FileSystemEventArgs args)
            {
                Notify(Normalize(args?.FullPath), watcherError: false);
            }

            private void OnRenamed(object sender, RenamedEventArgs args)
            {
                Notify(Normalize(args?.OldFullPath), watcherError: false);
                Notify(Normalize(args?.FullPath), watcherError: false);
            }

            private void OnError(object sender, ErrorEventArgs args)
            {
                List<KeyValuePair<string, Action<string, bool>>> callbacks;
                lock (_sync)
                {
                    callbacks = SnapshotCallbacksLocked();
                    DisposeWatcherLocked();
                    ScheduleRearmLocked();
                }

                _logger?.Warn(args?.GetException(), $"[InGameMonitor] File watcher failed for '{_directory}'; reconciling and rearming.");
                foreach (var callback in callbacks)
                {
                    SafeInvoke(callback.Value, callback.Key, watcherError: true);
                }
            }

            private void Notify(string path, bool watcherError)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    return;
                }

                List<Action<string, bool>> callbacks;
                lock (_sync)
                {
                    callbacks = _callbacks.TryGetValue(path, out var registered)
                        ? registered.ToList()
                        : null;
                }

                if (callbacks == null)
                {
                    return;
                }

                foreach (var callback in callbacks)
                {
                    SafeInvoke(callback, path, watcherError);
                }
            }

            private List<KeyValuePair<string, Action<string, bool>>> SnapshotCallbacksLocked()
            {
                return _callbacks
                    .SelectMany(pair => pair.Value.Select(callback =>
                        new KeyValuePair<string, Action<string, bool>>(pair.Key, callback)))
                    .ToList();
            }

            private static void SafeInvoke(Action<string, bool> callback, string path, bool watcherError)
            {
                try
                {
                    callback?.Invoke(path, watcherError);
                }
                catch
                {
                }
            }

            private void ScheduleRearmLocked()
            {
                if (_disposed || _rearmTimer != null)
                {
                    return;
                }

                var delay = RearmDelaysMs[Math.Min(_rearmAttempt, RearmDelaysMs.Length - 1)];
                _rearmAttempt++;
                _rearmTimer = new Timer(
                    _ =>
                    {
                        lock (_sync)
                        {
                            _rearmTimer?.Dispose();
                            _rearmTimer = null;
                        }

                        CreateWatcher();
                        lock (_sync)
                        {
                            if (!_disposed && _watcher == null)
                            {
                                ScheduleRearmLocked();
                            }
                        }
                    },
                    null,
                    delay,
                    Timeout.Infinite);
            }

            private void DisposeWatcherLocked()
            {
                var watcher = _watcher;
                _watcher = null;
                if (watcher == null)
                {
                    return;
                }

                try
                {
                    watcher.EnableRaisingEvents = false;
                    watcher.Dispose();
                }
                catch
                {
                }
            }

            public void Dispose()
            {
                lock (_sync)
                {
                    if (_disposed)
                    {
                        return;
                    }

                    _disposed = true;
                    _callbacks.Clear();
                    _rearmTimer?.Dispose();
                    _rearmTimer = null;
                    DisposeWatcherLocked();
                }
            }
        }

        private readonly object _sync = new object();
        private readonly ILogger _logger;
        private readonly Dictionary<string, DirectoryWatch> _directories =
            new Dictionary<string, DirectoryWatch>(StringComparer.OrdinalIgnoreCase);
        private bool _disposed;

        public ExactFileWatcherPool(ILogger logger)
        {
            _logger = logger;
        }

        public IDisposable Subscribe(string filePath, Action<string, bool> callback)
        {
            var path = Normalize(filePath);
            var directory = string.IsNullOrWhiteSpace(path) ? null : Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory) || callback == null)
            {
                return null;
            }

            lock (_sync)
            {
                if (_disposed)
                {
                    return null;
                }

                if (!_directories.TryGetValue(directory, out var watch))
                {
                    watch = new DirectoryWatch(directory, _logger);
                    _directories[directory] = watch;
                }

                watch.Add(path, callback);
                return new Subscription(this, directory, path, callback);
            }
        }

        private void Unsubscribe(string directory, string path, Action<string, bool> callback)
        {
            DirectoryWatch toDispose = null;
            lock (_sync)
            {
                if (!_directories.TryGetValue(directory, out var watch))
                {
                    return;
                }

                watch.Remove(path, callback);
                if (watch.IsEmpty)
                {
                    _directories.Remove(directory);
                    toDispose = watch;
                }
            }

            toDispose?.Dispose();
        }

        internal static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            try
            {
                return Path.GetFullPath(path.Trim());
            }
            catch
            {
                return path.Trim();
            }
        }

        public void Dispose()
        {
            List<DirectoryWatch> watches;
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                watches = _directories.Values.ToList();
                _directories.Clear();
            }

            foreach (var watch in watches)
            {
                watch.Dispose();
            }
        }
    }
}
