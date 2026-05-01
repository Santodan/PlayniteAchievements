using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Playnite.SDK;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Services.ThemeMigration
{
    /// <summary>
    /// Service for discovering themes that need to be migrated from SuccessStory.
    /// </summary>
    public sealed class ThemeDiscoveryService
    {
        private readonly ILogger _logger;
        private readonly IPlayniteAPI _playniteApi;
        private const string BackupFolderName = "PlayniteAchievements_backup";
        // Must match ThemeMigrationService.PluginExtensionId
        private const string PluginExtensionId = "PlayniteAchievementsSantodan";

        public ThemeDiscoveryService(ILogger logger, IPlayniteAPI playniteApi)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _playniteApi = playniteApi ?? throw new ArgumentNullException(nameof(playniteApi));
        }

        /// <summary>
        /// Represents a discovered theme.
        /// </summary>
        public class ThemeInfo
        {
            /// <summary>
            /// Display name from theme.yaml Name field.
            /// </summary>
            public string DisplayName { get; set; }

            /// <summary>
            /// Theme type (Desktop or Fullscreen).
            /// </summary>
            public string ThemeType { get; set; }

            /// <summary>
            /// Directory-based name (e.g., "Desktop/ThemeName").
            /// Used as fallback when DisplayName is not available.
            /// </summary>
            public string Name { get; set; }
            public string Path { get; set; }
            public bool HasBackup { get; set; }
            public bool NeedsMigration { get; set; }
            public bool CouldNotScan { get; set; }
            public string CurrentThemeVersion { get; set; }
            public string CachedMigratedThemeVersion { get; set; }
            public bool UpgradedSinceLastMigration { get; set; }

            /// <summary>
            /// Gets the best available name for display purposes.
            /// Combines ThemeType with DisplayName from theme.yaml if available,
            /// otherwise falls back to Name.
            /// </summary>
            public string BestDisplayName
            {
                get
                {
                    if (!string.IsNullOrWhiteSpace(DisplayName) && !string.IsNullOrWhiteSpace(ThemeType))
                    {
                        return $"{ThemeType}/{DisplayName}";
                    }
                    return Name;
                }
            }
        }

        /// <summary>
        /// Discovers all themes in the Playnite themes directory.
        /// </summary>
        /// <param name="themesRootPath">Root path to the themes directory.</param>
        /// <param name="themeMigrationVersionCache">
        /// Optional cache mapping ThemePath -> last migrated theme.yaml Version.
        /// When provided, discovery will flag themes that have upgraded versions since migration.
        /// </param>
        /// <returns>List of discovered themes.</returns>
        public List<ThemeInfo> DiscoverThemes(string themesRootPath, IReadOnlyDictionary<string, ThemeMigrationCacheEntry> themeMigrationVersionCache = null)
        {
            var themes = new List<ThemeInfo>();

            if (string.IsNullOrWhiteSpace(themesRootPath))
            {
                _logger.Warn("Themes root path is empty or null.");
                return themes;
            }

            if (!Directory.Exists(themesRootPath))
            {
                _logger.Warn($"Themes directory does not exist: {themesRootPath}");
                return themes;
            }

            try
            {
                // Playnite themes are organized in Desktop and Fullscreen subdirectories
                var subDirectories = new[] { "Desktop", "Fullscreen" };

                foreach (var subDir in subDirectories)
                {
                    var subDirPath = Path.Combine(themesRootPath, subDir);
                    if (!Directory.Exists(subDirPath))
                    {
                        _logger.Debug($"Theme subdirectory does not exist: {subDirPath}");
                        continue;
                    }

                    var themeDirectories = Directory.GetDirectories(subDirPath);
                    // _logger.Info($"Found {themeDirectories.Length} themes in {subDir}");

                    foreach (var themeDir in themeDirectories)
                    {
                        var dirInfo = new DirectoryInfo(themeDir);
                        // Strip ID suffix from directory name (e.g., "Stellar_ab1234" -> "Stellar")
                        var cleanDirName = StripThemeIdSuffix(dirInfo.Name);
                        var themeName = $"{subDir}/{cleanDirName}";

                        // _logger.Debug($"Processing theme: {themeName} at {themeDir}");

                        var backupPath = Path.Combine(themeDir, BackupFolderName);
                        var hasBackup = Directory.Exists(backupPath);
                        // _logger.Debug($"Theme has backup: {hasBackup}");

                        // Read theme.yaml version/name first for display/cache metadata.
                        string currentVersion = null;
                        ThemeYamlVersionReader.TryReadThemeVersion(themeDir, out currentVersion);

                        string displayName = null;
                        ThemeYamlVersionReader.TryReadThemeName(themeDir, out displayName);

                        // Check if theme contains legacy references that still need migration.
                        var (needsMigration, couldNotScan, hasNativePlayniteAchievementsSupport) = CheckIfNeedsMigration(themeDir);

                        // Compare against cached migrated version (optional)
                        string cachedVersion = null;
                        bool upgradedSinceLastMigration = false;
                        if (themeMigrationVersionCache != null &&
                            themeMigrationVersionCache.TryGetValue(themeDir, out var cached) &&
                            cached != null &&
                            !string.IsNullOrWhiteSpace(cached.MigratedThemeVersion) &&
                            !string.IsNullOrWhiteSpace(currentVersion))
                        {
                            cachedVersion = cached.MigratedThemeVersion;
                            upgradedSinceLastMigration = !string.Equals(cachedVersion, currentVersion, StringComparison.OrdinalIgnoreCase);
                        }

                        var themeInfo = new ThemeInfo
                        {
                            DisplayName = displayName,
                            ThemeType = subDir,
                            Name = themeName,
                            Path = themeDir,
                            HasBackup = hasBackup,
                            // Mixed themes exist in the wild: a theme can already reference
                            // PlayniteAchievementsSantodan in one file and still keep legacy
                            // SuccessStory/old-PA gates in another. Native support should not
                            // suppress migration when any migratable reference remains.
                            NeedsMigration = needsMigration,
                            CouldNotScan = couldNotScan,
                            CurrentThemeVersion = currentVersion,
                            CachedMigratedThemeVersion = cachedVersion,
                            UpgradedSinceLastMigration = upgradedSinceLastMigration
                        };

                        themes.Add(themeInfo);

                        // _logger.Info($"Discovered theme: {themeName}, NeedsMigration: {themeInfo.NeedsMigration}, HasBackup: {hasBackup}, CouldNotScan: {couldNotScan}");
                    }
                }

                // _logger.Info($"Discovered {themes.Count} themes, {themes.Count(t => t.NeedsMigration)} need migration.");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Failed to discover themes in: {themesRootPath}");
            }

            return themes;
        }

        /// <summary>
        /// Strips the ID suffix from a theme directory name.
        /// Playnite theme directories often have IDs like "Stellar_ab1234" where
        /// "_ab1234" is an ID suffix that should be removed for display purposes.
        /// </summary>
        private static string StripThemeIdSuffix(string directoryName)
        {
            if (string.IsNullOrWhiteSpace(directoryName))
            {
                return directoryName;
            }

            // Pattern: underscore followed by 6+ alphanumeric characters at the end
            // e.g., "Stellar_ab1234" -> "Stellar"
            var lastUnderscoreIndex = directoryName.LastIndexOf('_');
            if (lastUnderscoreIndex > 0 && lastUnderscoreIndex < directoryName.Length - 1)
            {
                var suffix = directoryName.Substring(lastUnderscoreIndex + 1);
                // Check if suffix looks like an ID (alphanumeric, reasonable length)
                if (suffix.Length >= 6 && suffix.Length <= 12 && suffix.All(char.IsLetterOrDigit))
                {
                    return directoryName.Substring(0, lastUnderscoreIndex);
                }
            }

            return directoryName;
        }

        /// <summary>
        /// Gets the default Playnite themes directory path.
        /// </summary>
        /// <returns>Path to the themes directory, or null if not found.</returns>
        public IReadOnlyList<string> GetDefaultThemesPaths()
        {
            var roots = new List<string>();

            try
            {
                var configPath = _playniteApi.Paths.ConfigurationPath;
                var configThemesPath = Path.Combine(configPath, "Themes");
                if (Directory.Exists(configThemesPath))
                {
                    roots.Add(configThemesPath);
                }

                var applicationThemesPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Themes");
                if (Directory.Exists(applicationThemesPath))
                {
                    roots.Add(applicationThemesPath);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to get default themes paths.");
            }

            return roots
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public string GetDefaultThemesPath()
        {
            return GetDefaultThemesPaths().FirstOrDefault();
        }

        public List<ThemeInfo> DiscoverDefaultThemes(IReadOnlyDictionary<string, ThemeMigrationCacheEntry> themeMigrationVersionCache = null)
        {
            var discoveredThemes = new Dictionary<string, ThemeInfo>(StringComparer.OrdinalIgnoreCase);

            foreach (var themesRootPath in GetDefaultThemesPaths())
            {
                foreach (var theme in DiscoverThemes(themesRootPath, themeMigrationVersionCache))
                {
                    if (theme?.Path == null)
                    {
                        continue;
                    }

                    discoveredThemes[theme.Path] = theme;
                }
            }

            return discoveredThemes.Values
                .OrderBy(theme => theme.BestDisplayName ?? theme.Name)
                .ToList();
        }

        /// <summary>
        /// Checks if a theme directory contains migratable legacy references.
        /// </summary>
        private (bool needsMigration, bool couldNotScan, bool hasNativePlayniteAchievementsSupport) CheckIfNeedsMigration(string themePath)
        {
            int filesRead = 0;
            int filesSkipped = 0;
            bool foundMigratableReference = false;
            bool foundNativePlayniteAchievementsSupport = false;

            try
            {
                var dirInfo = new DirectoryInfo(themePath);

                // Search recursively for XAML, PS1, and CS files
                var filesToCheck = new List<string>();

                try
                {
                    filesToCheck.AddRange(dirInfo.GetFiles("*.xaml", SearchOption.AllDirectories).Select(f => f.FullName));
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, $"Could not search for XAML files in {themePath}");
                }

                try
                {
                    filesToCheck.AddRange(dirInfo.GetFiles("*.ps1", SearchOption.AllDirectories).Select(f => f.FullName));
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, $"Could not search for PS1 files in {themePath}");
                }

                try
                {
                    filesToCheck.AddRange(dirInfo.GetFiles("*.cs", SearchOption.AllDirectories).Select(f => f.FullName));
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, $"Could not search for CS files in {themePath}");
                }

                // Remove duplicates and filter out backup folder
                filesToCheck = filesToCheck
                    .Where(f => !f.Contains(BackupFolderName))
                    .Distinct()
                    .ToList();

                // _logger.Debug($"Found {filesToCheck.Count} files to check in theme: {themePath}");

                foreach (var file in filesToCheck.Take(100))
                {
                    try
                    {
                        var content = File.ReadAllText(file);
                        filesRead++;

                        if (ContainsNativePlayniteAchievementsSupport(content))
                        {
                            foundNativePlayniteAchievementsSupport = true;
                        }

                        if (ContainsMigratableReference(content))
                        {
                            foundMigratableReference = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        filesSkipped++;
                        _logger.Debug(ex, $"Could not read file while checking theme: {file}");
                    }
                }

                // _logger.Info($"Theme scan for {Path.GetFileName(themePath)}: {filesRead} files read, {filesSkipped} files skipped, found migratable references: {foundMigratableReference}");

                // If we couldn't read ANY files, conservatively assume it needs migration
                // This handles the case where the theme is currently running and files are locked
                if (filesRead == 0 && filesSkipped > 0)
                {
                    _logger.Warn($"Could not read any files in theme (likely currently running): {themePath}");
                    return (true, true, false); // Conservative: assume it needs migration, mark as could not scan
                }

                return (foundMigratableReference, false, foundNativePlayniteAchievementsSupport);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Failed to check if theme needs migration: {themePath}");
                return (true, true, false); // Conservative: assume it needs migration on error, mark as could not scan
            }
        }

        private static bool ContainsMigratableReference(string content)
        {
            if (string.IsNullOrEmpty(content))
            {
                return false;
            }

            // Themes built for the original PlayniteAchievements fork (e.g. Aniki ReMake) write
            // "PluginStatus Plugin=PlayniteAchievements" for install checks.  PluginStatus resolves
            // against the extension ID, not the SourceName, so this always returns false in this fork
            // (ID = PlayniteAchievementsSantodan).  Detect and flag them for migration.
            // Themes built for the original PlayniteAchievements fork write
            // "PluginStatus Plugin=PlayniteAchievements" or "PluginStatus Plugin=""PlayniteAchievements"""
            // for install checks.  PluginStatus resolves against the extension ID, not the SourceName,
            // so these always return false in this fork (ID = PlayniteAchievementsSantodan).
            if (Regex.IsMatch(content, @"PluginStatus\s+Plugin\s*=\s*['""]?PlayniteAchievements(?!Santodan)", RegexOptions.IgnoreCase))
            {
                return true;
            }

            if (content.IndexOf("PlayniteAchievementsSantodanSantodan", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            // Normalize historical migrations that rewrote PluginSettings source aliases
            // to the extension ID. Theme source names should stay on PlayniteAchievements.
            if (Regex.IsMatch(content, @"PluginSettings\s+Plugin\s*=\s*['""]?PlayniteAchievementsSantodan['""]?", RegexOptions.IgnoreCase))
            {
                return true;
            }

            if (content.IndexOf("SuccessStoryFullscreenHelper", StringComparison.OrdinalIgnoreCase) >= 0 ||
                content.IndexOf("playnite-successstory-plugin", StringComparison.OrdinalIgnoreCase) >= 0 ||
                content.IndexOf("SSHelper", StringComparison.OrdinalIgnoreCase) >= 0 ||
                content.IndexOf("SuccessStory", StringComparison.OrdinalIgnoreCase) >= 0 ||
                content.IndexOf("\uE820", StringComparison.Ordinal) >= 0 ||
                content.IndexOf("&#xE820;", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            foreach (var controlName in ControlMappings.LegacyToModernControlNames.Keys)
            {
                if (content.IndexOf($"SuccessStory_{controlName}", StringComparison.Ordinal) >= 0 ||
                    Regex.IsMatch(content, GetStandaloneControlNamePattern(controlName)))
                {
                    return true;
                }
            }

            foreach (var bindingPath in ControlMappings.LegacyToModernBindingPaths.Keys)
            {
                if (content.IndexOf($"LegacyData.{bindingPath}", StringComparison.Ordinal) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool ContainsNativePlayniteAchievementsSupport(string content)
        {
            if (string.IsNullOrEmpty(content))
            {
                return false;
            }

            // Only treat as already-native if the theme explicitly references the Santodan extension ID.
            // Themes that only contain the SourceName "PlayniteAchievements" (e.g. PluginSettings bindings)
            // still need the PluginStatus install-check bindings fixed.
            return content.IndexOf(PluginExtensionId, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string GetStandaloneControlNamePattern(string controlName)
        {
            return $@"(?<![A-Za-z0-9])_{Regex.Escape(controlName)}(?![A-Za-z0-9])";
        }
    }
}

