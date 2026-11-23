using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Playnite.SDK;

namespace PlayniteAchievements.Services.ThemeMigration
{
    /// <summary>
    /// Holds one theme discovery result for the Playnite session. Playnite loads themes only at
    /// startup, so the startup scan stays valid; the migration service refreshes the single theme
    /// it changes. The first caller starts the scan on a worker thread and later callers share it.
    /// </summary>
    public sealed class ThemeDiscoveryCache
    {
        private readonly ThemeDiscoveryService _discovery;
        private readonly object _gate = new object();
        private Task<IReadOnlyList<ThemeDiscoveryService.ThemeInfo>> _themes;

        public ThemeDiscoveryCache(ILogger logger, IPlayniteAPI playniteApi)
        {
            _discovery = new ThemeDiscoveryService(logger, playniteApi);
        }

        /// <summary>
        /// Returns the discovered themes, starting the scan if none has run. A failed scan is
        /// retried on the next call.
        /// </summary>
        public Task<IReadOnlyList<ThemeDiscoveryService.ThemeInfo>> GetThemesAsync()
        {
            lock (_gate)
            {
                if (_themes == null || _themes.IsFaulted || _themes.IsCanceled)
                {
                    _themes = Task.Run(() => Scan());
                }

                return _themes;
            }
        }

        /// <summary>
        /// Rescans one theme directory and replaces its entry, keeping its position. A directory
        /// that no longer exists is dropped.
        /// </summary>
        public async Task RefreshThemeAsync(string themePath)
        {
            if (string.IsNullOrWhiteSpace(themePath))
            {
                return;
            }

            await GetThemesAsync().ConfigureAwait(false);
            var updated = await Task.Run(() => _discovery.DiscoverTheme(themePath)).ConfigureAwait(false);

            lock (_gate)
            {
                // Completed: awaited above, and only ever replaced by completed tasks after success.
                var next = _themes.Result.ToList();
                var index = next.FindIndex(t => string.Equals(t.Path, themePath, StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                {
                    next.RemoveAt(index);
                }

                if (updated != null)
                {
                    next.Insert(index >= 0 ? index : next.Count, updated);
                }

                _themes = Task.FromResult<IReadOnlyList<ThemeDiscoveryService.ThemeInfo>>(next);
            }
        }

        private IReadOnlyList<ThemeDiscoveryService.ThemeInfo> Scan()
        {
            // Santodan-Fork: scan both the user and application theme folders.
            return _discovery.DiscoverDefaultThemes();
        }
    }
}
