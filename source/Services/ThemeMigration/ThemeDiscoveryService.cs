using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Playnite.SDK;

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

        private static readonly HashSet<string> ScannedExtensions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".xaml", ".ps1", ".cs" };

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

            /// <summary>
            /// True when the theme already targets PlayniteAchievements and uses legacy
            /// controls or LegacyData bindings that Full or Custom migration can modernize.
            /// </summary>
            public bool ModernizeOnly { get; set; }
            public string CurrentThemeVersion { get; set; }

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
        /// <returns>List of discovered themes, Desktop themes first.</returns>
        public List<ThemeInfo> DiscoverThemes(string themesRootPath)
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
                foreach (var subDir in new[] { "Desktop", "Fullscreen" })
                {
                    var subDirPath = Path.Combine(themesRootPath, subDir);
                    if (!Directory.Exists(subDirPath))
                    {
                        _logger.Debug($"Theme subdirectory does not exist: {subDirPath}");
                        continue;
                    }

                    foreach (var themeDir in Directory.GetDirectories(subDirPath))
                    {
                        themes.Add(CreateThemeInfo(themeDir, subDir));
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Failed to discover themes in: {themesRootPath}");
            }

            return themes;
        }

        /// <summary>
        /// Discovers one theme directory, for refreshing a single theme after it changed.
        /// Returns null when the directory no longer exists.
        /// </summary>
        public ThemeInfo DiscoverTheme(string themeDir)
        {
            if (string.IsNullOrWhiteSpace(themeDir) || !Directory.Exists(themeDir))
            {
                return null;
            }

            // The parent folder is the theme type: Themes/Desktop/<theme> or Themes/Fullscreen/<theme>.
            var themeType = Path.GetFileName(Path.GetDirectoryName(themeDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
            return CreateThemeInfo(themeDir, themeType);
        }

        private ThemeInfo CreateThemeInfo(string themeDir, string themeType)
        {
            var dirInfo = new DirectoryInfo(themeDir);
            // Strip ID suffix from directory name (e.g., "Stellar_ab1234" -> "Stellar")
            var cleanDirName = StripThemeIdSuffix(dirInfo.Name);

            var hasBackup = Directory.Exists(Path.Combine(themeDir, BackupFolderName));

            ThemeYamlVersionReader.TryReadThemeVersion(themeDir, out var currentVersion);
            ThemeYamlVersionReader.TryReadThemeName(themeDir, out var displayName);

            // Check if theme contains legacy references that still need migration.
            var (needsMigration, couldNotScan, hasNativePlayniteAchievementsSupport, hasLegacyPlayniteAchievementsReference) = ScanTheme(themeDir, _logger);

            // A theme that already targets PlayniteAchievements through legacy controls can be modernized.
            // Fullscreen themes only support Limited migration, which has nothing to change for them.
            var modernizeOnly = hasNativePlayniteAchievementsSupport && hasLegacyPlayniteAchievementsReference;
            var isFullscreenTheme = string.Equals(themeType, "Fullscreen", StringComparison.OrdinalIgnoreCase);

            return new ThemeInfo
            {
                DisplayName = displayName,
                ThemeType = themeType,
                Name = $"{themeType}/{cleanDirName}",
                Path = themeDir,
                HasBackup = hasBackup,
                NeedsMigration = !hasBackup &&
                                 ((needsMigration && !hasNativePlayniteAchievementsSupport) ||
                                  (modernizeOnly && !isFullscreenTheme)),
                CouldNotScan = couldNotScan &&
                               !hasNativePlayniteAchievementsSupport,
                ModernizeOnly = modernizeOnly,
                CurrentThemeVersion = currentVersion
            };
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
        public string GetDefaultThemesPath()
        {
            try
            {
                // Playnite stores themes in the configuration directory
                var configPath = _playniteApi.Paths.ConfigurationPath;
                var themesPath = Path.Combine(configPath, "Themes");
                return Directory.Exists(themesPath) ? themesPath : null;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to get default themes path.");
                return null;
            }
        }

        /// <summary>
        /// Scans a theme directory for migratable legacy references, native PlayniteAchievements
        /// support, and legacy PlayniteAchievements controls or bindings.
        /// </summary>
        internal static (bool needsMigration, bool couldNotScan, bool hasNativePlayniteAchievementsSupport, bool hasLegacyPlayniteAchievementsReference) ScanTheme(string themePath, ILogger logger)
        {
            int filesRead = 0;
            int filesSkipped = 0;
            bool foundMigratableReference = false;
            bool foundNativePlayniteAchievementsSupport = false;
            bool foundLegacyPlayniteAchievementsReference = false;

            try
            {
                var dirInfo = new DirectoryInfo(themePath);

                // One walk of the theme folder for the XAML, PS1 and CS files, outside the backup folder.
                var filesToCheck = new List<string>();
                try
                {
                    foreach (var file in dirInfo.EnumerateFiles("*", SearchOption.AllDirectories))
                    {
                        if (ScannedExtensions.Contains(file.Extension) &&
                            file.FullName.IndexOf(BackupFolderName, StringComparison.Ordinal) < 0)
                        {
                            filesToCheck.Add(file.FullName);
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger?.Debug(ex, $"Could not list all files in {themePath}");
                }

                foreach (var file in filesToCheck)
                {
                    try
                    {
                        var content = File.ReadAllText(file);
                        filesRead++;

                        // Each flag only ever turns on, so a check already satisfied by an
                        // earlier file is skipped.
                        if (!foundNativePlayniteAchievementsSupport &&
                            ContainsNativePlayniteAchievementsSupport(content))
                        {
                            foundNativePlayniteAchievementsSupport = true;
                        }

                        if (!foundLegacyPlayniteAchievementsReference &&
                            ContainsLegacyPlayniteAchievementsReference(content))
                        {
                            foundLegacyPlayniteAchievementsReference = true;
                        }

                        if (!foundMigratableReference &&
                            ContainsMigratableReference(content))
                        {
                            foundMigratableReference = true;
                        }

                        if (foundNativePlayniteAchievementsSupport && foundLegacyPlayniteAchievementsReference)
                        {
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        filesSkipped++;
                        logger?.Debug(ex, $"Could not read file while checking theme: {file}");
                    }
                }

                // _logger.Info($"Theme scan for {Path.GetFileName(themePath)}: {filesRead} files read, {filesSkipped} files skipped, found migratable references: {foundMigratableReference}");

                // If we couldn't read ANY files, conservatively assume it needs migration
                // This handles the case where the theme is currently running and files are locked
                if (filesRead == 0 && filesSkipped > 0)
                {
                    logger?.Warn($"Could not read any files in theme (likely currently running): {themePath}");
                    return (true, true, false, false); // Conservative: assume it needs migration, mark as could not scan
                }

                return (foundMigratableReference, false, foundNativePlayniteAchievementsSupport, foundLegacyPlayniteAchievementsReference);
            }
            catch (Exception ex)
            {
                logger?.Error(ex, $"Failed to check if theme needs migration: {themePath}");
                return (true, true, false, false); // Conservative: assume it needs migration on error, mark as could not scan
            }
        }

        private static bool ContainsLegacyPlayniteAchievementsReference(string content)
        {
            if (string.IsNullOrEmpty(content))
            {
                return false;
            }

            foreach (var controlName in ControlMappings.LegacyToModernControlNames.Keys)
            {
                if (content.IndexOf($"PlayniteAchievements_{controlName}", StringComparison.Ordinal) >= 0)
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

        private static bool ContainsMigratableReference(string content)
        {
            if (string.IsNullOrEmpty(content))
            {
                return false;
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

            return content.IndexOf("PlayniteAchievements", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string GetStandaloneControlNamePattern(string controlName)
        {
            return $@"(?<![A-Za-z0-9])_{Regex.Escape(controlName)}(?![A-Za-z0-9])";
        }
    }
}

