using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Services.ThemeMigration
{
    internal sealed class ThemeAutoMigrationService
    {
        private readonly ILogger _logger;
        private readonly IPlayniteAPI _playniteApi;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly Action _saveSettings;
        private readonly Action<string> _notifyThemeAutoMigrated;

        public ThemeAutoMigrationService(
            ILogger logger,
            IPlayniteAPI playniteApi,
            PlayniteAchievementsSettings settings,
            Action saveSettings,
            Action<string> notifyThemeAutoMigrated)
        {
            _logger = logger;
            _playniteApi = playniteApi;
            _settings = settings;
            _saveSettings = saveSettings;
            _notifyThemeAutoMigrated = notifyThemeAutoMigrated;
        }

        public void ScheduleAutoMigration()
        {
            try
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var themesDiscovery = new ThemeDiscoveryService(_logger, _playniteApi);
                        var themes = themesDiscovery.DiscoverDefaultThemes(_settings?.Persisted?.ThemeMigrationVersionCache);
                        if (themes.Count == 0)
                        {
                            return;
                        }

                        var persisted = _settings?.Persisted;
                        var wasCacheEmpty = persisted != null &&
                                            (persisted.ThemeMigrationVersionCache == null || persisted.ThemeMigrationVersionCache.Count == 0);

                        if (wasCacheEmpty)
                        {
                            var seededThemes = themes
                                .Where(t => t.HasBackup && !string.IsNullOrWhiteSpace(t.CurrentThemeVersion))
                                .ToList();

                            if (seededThemes.Count > 0)
                            {
                                foreach (var theme in seededThemes)
                                {
                                    persisted.ThemeMigrationVersionCache[theme.Path] = new ThemeMigrationCacheEntry
                                    {
                                        ThemePath = theme.Path,
                                        ThemeName = theme.BestDisplayName,
                                        MigratedThemeVersion = theme.CurrentThemeVersion,
                                        MigratedAtUtc = DateTime.UtcNow
                                    };
                                }

                                _saveSettings?.Invoke();
                                _logger.Info($"Seeded ThemeMigrationVersionCache with {seededThemes.Count} existing migrated themes.");
                            }
                        }

                        var cache = _settings?.Persisted?.ThemeMigrationVersionCache;
                        var upgraded = themes
                            .Where(t => t != null && t.NeedsMigration && !string.IsNullOrWhiteSpace(t.CurrentThemeVersion))
                            .Where(t => cache != null && cache.TryGetValue(t.Path, out var cached) &&
                                        !string.IsNullOrWhiteSpace(cached.MigratedThemeVersion) &&
                                        !string.Equals(cached.MigratedThemeVersion, t.CurrentThemeVersion, StringComparison.OrdinalIgnoreCase))
                            .ToList();

                        // Also migrate themes that have never been migrated (not in cache at all)
                        var firstTime = themes
                            .Where(t => t != null && t.NeedsMigration)
                            .Where(t => cache == null || !cache.ContainsKey(t.Path))
                            .ToList();

                        if (upgraded.Count == 0 && firstTime.Count == 0)
                        {
                            return;
                        }

                        var migrationService = new ThemeMigrationService(
                            _logger,
                            _settings,
                            _saveSettings);

                        var migratedThemes = new List<string>();

                        foreach (var theme in upgraded)
                        {
                            try
                            {
                                var result = await migrationService.MigrateThemeAsync(theme.Path);
                                if (result.Success)
                                {
                                    _logger.Info($"Auto-migrated upgraded theme: {theme.Name}");
                                    migratedThemes.Add(theme.BestDisplayName);
                                }
                                else
                                {
                                    _logger.Warn($"Failed to auto-migrate upgraded theme '{theme.Name}': {result.Message}");
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.Error(ex, $"Exception auto-migrating upgraded theme: {theme.Name}");
                            }
                        }

                        foreach (var theme in firstTime)
                        {
                            try
                            {
                                var result = await migrationService.MigrateThemeAsync(theme.Path);
                                if (result.Success)
                                {
                                    _logger.Info($"Auto-migrated first-time theme: {theme.Name}");
                                    migratedThemes.Add(theme.BestDisplayName);
                                }
                                else
                                {
                                    _logger.Warn($"Failed to auto-migrate first-time theme '{theme.Name}': {result.Message}");
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.Error(ex, $"Exception auto-migrating first-time theme: {theme.Name}");
                            }
                        }

                        NotifyMigratedThemes(migratedThemes);
                    }
                    catch (Exception ex)
                    {
                        _logger?.Debug(ex, "Failed startup theme auto-migration.");
                    }
                });
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to schedule startup theme auto-migration.");
            }
        }

        private void NotifyMigratedThemes(List<string> migratedThemes)
        {
            if (migratedThemes == null || migratedThemes.Count == 0)
            {
                return;
            }

            var dispatcher = _playniteApi?.MainView?.UIDispatcher ?? Application.Current?.Dispatcher;
            if (dispatcher != null)
            {
                _ = dispatcher.BeginInvoke(new Action(() =>
                {
                    foreach (var themeName in migratedThemes)
                    {
                        _notifyThemeAutoMigrated?.Invoke(themeName);
                    }
                }));
                return;
            }

            foreach (var themeName in migratedThemes)
            {
                _notifyThemeAutoMigrated?.Invoke(themeName);
            }
        }
    }
}
