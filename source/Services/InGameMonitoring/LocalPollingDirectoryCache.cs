using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PlayniteAchievements.Services.InGameMonitoring
{
    // Owned by one game-monitor session. Cache topology, never achievement contents.
    internal sealed class LocalPollingDirectoryCache
    {
        private readonly object _sync = new object();
        private string _key;
        private string[] _folders;
        private Dictionary<string, DateTime> _directories;

        public string[] GetOrDiscover(string key, Func<Action<string>, IEnumerable<string>> discover)
        {
            lock (_sync)
            {
                if (_folders != null && string.Equals(key, _key, StringComparison.Ordinal) &&
                    _directories.All(pair => TryStamp(pair.Key, out var stamp) && stamp == pair.Value))
                    return _folders.ToArray();

                var directories = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
                var reliable = true;
                var folders = discover(path =>
                {
                    // Record before enumeration: a concurrent mutation invalidates the next read.
                    if (TryStamp(path, out var stamp)) directories[path] = stamp;
                    else reliable = false;
                }).ToArray();
                _key = key;
                _directories = directories;
                _folders = reliable && directories.Count > 0 ? folders : null;
                return folders;
            }
        }

        private static bool TryStamp(string path, out DateTime stamp)
        {
            stamp = default;
            if (string.IsNullOrWhiteSpace(path)) return false;
            try
            {
                var directory = new DirectoryInfo(path);
                if (!directory.Exists) return false;
                stamp = directory.LastWriteTimeUtc;
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
    }
}
