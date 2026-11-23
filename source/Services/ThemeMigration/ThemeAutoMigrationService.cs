using ForkLog = PlayniteAchievements.Services.Logging.ForkLogger;
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
        private readonly ThemeDiscoveryCache _discoveredThemes;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly Action _saveSettings;
        private readonly Action<string> _notifyThemeAutoMigrated;

        public ThemeAutoMigrationService(
            ILogger logger,
            IPlayniteAPI playniteApi,
            ThemeDiscoveryCache discoveredThemes,
            PlayniteAchievementsSettings settings,
            Action saveSettings,
            Action<string> notifyThemeAutoMigrated)
        {
            _logger = logger;
            _playniteApi = playniteApi;
            _discoveredThemes = discoveredThemes ?? throw new ArgumentNullException(nameof(discoveredThemes));
            _settings = settings;
            _saveSettings = saveSettings;
            _notifyThemeAutoMigrated = notifyThemeAutoMigrated;
        }

        public void ScheduleAutoMigration()
        {
            if (_settings?.Persisted?.EnableAutomaticThemeMigration != true)
            {
                ForkLog.Wrap(_logger)?.Info("Automatic theme migration is disabled.");
                return;
            }

            try
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        // This scan fills the shared discovery result, so the settings and
                        // first-time setup theme lists open without scanning again.
                        var themes = await _discoveredThemes.GetThemesAsync();

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

                        // Also migrate themes that have never been migrated (not in cache at all).
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
                            _saveSettings,
                            _discoveredThemes);

                        var migratedThemes = new List<string>();

                        foreach (var theme in upgraded)
                        {
                            try
                            {
                                var result = await migrationService.MigrateThemeAsync(
                                    theme.Path,
                                    ThemeMigrationService.IsFullscreenThemePath(theme.Path)
                                        ? MigrationMode.Limited
                                        : persisted.ThemeMigrationMode,
                                    CustomMigrationSelection.FromSettings(persisted));
                                if (result.Success && result.FilesBackedUp > 0)
                                {
                                    _logger.Info($"Auto-migrated upgraded theme: {theme.Name}");
                                    migratedThemes.Add(theme.BestDisplayName);
                                }
                                else if (result.Success)
                                {
                                    _logger.Info($"Upgraded theme needed no changes: {theme.Name}");
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
                                var result = await migrationService.MigrateThemeAsync(
                                    theme.Path,
                                    ThemeMigrationService.IsFullscreenThemePath(theme.Path)
                                        ? MigrationMode.Limited
                                        : persisted.ThemeMigrationMode,
                                    CustomMigrationSelection.FromSettings(persisted));
                                if (result.Success)
                                {
                                    ForkLog.Wrap(_logger).Info($"Auto-migrated first-time theme: {theme.Name}");
                                    migratedThemes.Add(theme.BestDisplayName);
                                }
                                else
                                {
                                    ForkLog.Wrap(_logger).Warn($"Failed to auto-migrate first-time theme '{theme.Name}': {result.Message}");
                                }
                            }
                            catch (Exception ex)
                            {
                                ForkLog.Wrap(_logger).Error(ex, $"Exception auto-migrating first-time theme: {theme.Name}");
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
