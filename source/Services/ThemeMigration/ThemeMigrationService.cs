using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using Playnite.SDK;

namespace PlayniteAchievements.Services.ThemeMigration
{
    /// <summary>
    /// Service for migrating themes from SuccessStory to PlayniteAchievements.
    /// Creates a selective backup of only files that were changed.
    /// </summary>
    public sealed class ThemeMigrationService
    {
        private readonly ILogger _logger;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly Action _saveSettings;
        private const string BackupFolderName = "PlayniteAchievements_backup";
        private const string ManifestFileName = "backup_manifest.txt";

        /// <summary>
        /// The extension ID of this plugin as declared in extension.yaml.
        /// Used when rewriting installation-check bindings in themes (e.g.
        /// <c>[playnite-successstory-plugin].IsInstalled</c>) so that Playnite's
        /// <c>ExtensionsStatusBinder</c> can match against <c>Description.Id</c>.
        /// This is distinct from <c>ThemeSourceName</c>, which is the SourceName
        /// registered via <c>AddCustomElementSupport</c> / <c>AddSettingsSupport</c>
        /// and used for control-name prefixes such as <c>PlayniteAchievements_PluginButton</c>.
        /// </summary>
        private const string PluginExtensionId = "PlayniteAchievementsSantodan";

        /// <summary>
        /// The SourceName registered with Playnite's theme system via
        /// <c>AddCustomElementSupport</c> and <c>AddSettingsSupport</c>.
        /// Theme control names use this prefix (e.g. <c>PlayniteAchievements_PluginButton</c>),
        /// and <c>PluginSettings</c> bindings reference it (e.g. <c>Plugin=PlayniteAchievements</c>).
        /// </summary>
        private const string ThemeSourceName = "PlayniteAchievements";

        /// <summary>
        /// Binary file extensions that should never be processed.
        /// These files should not be read as text or modified.
        /// </summary>
        private static readonly HashSet<string> BinaryExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Images
            ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".webp", ".svg", ".psd", ".tiff",
            // Executables and binaries
            ".exe", ".dll", ".so", ".dylib", ".bin",
            // Archives and packages
            ".zip", ".rar", ".7z", ".tar", ".gz", ".pext",
            // Fonts
            ".ttf", ".otf", ".woff", ".woff2", ".eot",
            // Audio/Video
            ".mp3", ".mp4", ".wav", ".avi", ".mkv", ".flac", ".ogg", ".mov",
            // Documents (binary formats)
            ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx"
        };

        public ThemeMigrationService(ILogger logger, PlayniteAchievementsSettings settings = null, Action saveSettings = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settings = settings;
            _saveSettings = saveSettings;
        }

        /// <summary>
        /// Migrates a theme from SuccessStory to PlayniteAchievements.
        /// </summary>
        /// <param name="themePath">Full path to the theme directory.</param>
        /// <param name="mode">The migration mode to use (Limited for text-only, Full for complete modernization, Custom for partial modernization).</param>
        /// <param name="customSelection">Selected modern elements when using Custom mode.</param>
        /// <returns>Migration result with status and details.</returns>
        public async Task<MigrationResult> MigrateThemeAsync(
            string themePath,
            MigrationMode mode = MigrationMode.Limited,
            CustomMigrationSelection customSelection = null)
        {
            if (string.IsNullOrWhiteSpace(themePath))
            {
                return new MigrationResult
                {
                    Success = false,
                    Message = "Theme path cannot be empty."
                };
            }

            if (!Directory.Exists(themePath))
            {
                return new MigrationResult
                {
                    Success = false,
                    Message = $"Theme directory does not exist: {themePath}"
                };
            }

            _logger.Info($"Starting theme migration for: {themePath}");

            try
            {
                var backupPath = Path.Combine(themePath, BackupFolderName);

                if (Directory.Exists(backupPath))
                {
                    _logger.Info($"Backup already exists at: {backupPath}");
                    return new MigrationResult
                    {
                        Success = false,
                        Message = $"Backup folder '{BackupFolderName}' already exists in theme directory. Please remove it first."
                    };
                }

                var result = await Task.Run(() =>
                {
                    return PerformMigration(themePath, backupPath, mode, customSelection);
                });

                // If no files needed changes, report success but note no backup was created
                if (result.FilesBackedUp == 0)
                {
                    _logger.Info($"No files contained SuccessStory references - no changes needed for: {themePath}");

                    // Even if no changes were required, cache the theme version so we can detect upgrades later.
                    TryUpdateThemeMigrationVersionCache(themePath);

                    return new MigrationResult
                    {
                        Success = true,
                        Message = "Theme already compatible - no SuccessStory references found. No backup created.",
                        Mode = mode,
                        FilesProcessed = 0,
                        ReplacementsMade = 0,
                        FilesBackedUp = 0
                    };
                }

                _logger.Info($"Theme migration completed successfully for: {themePath}");

                // Cache the migrated theme's version in plugin settings so upgrades can be detected on startup.
                TryUpdateThemeMigrationVersionCache(themePath);

                return new MigrationResult
                {
                    Success = true,
                    Message = BuildSuccessMessage(result, mode),
                    Mode = mode,
                    BackupPath = backupPath,
                    FilesProcessed = result.FilesProcessed,
                    ReplacementsMade = result.ReplacementsMade,
                    ControlReplacementsMade = result.ControlReplacementsMade,
                    BindingReplacementsMade = result.BindingReplacementsMade,
                    FilesBackedUp = result.FilesBackedUp
                };
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Failed to migrate theme at: {themePath}");
                return new MigrationResult
                {
                    Success = false,
                    Message = $"Migration failed: {ex.Message}"
                };
            }
        }

        private void TryUpdateThemeMigrationVersionCache(string themePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(themePath) || _settings?.Persisted == null)
                {
                    return;
                }

                if (!ThemeYamlVersionReader.TryReadThemeVersion(themePath, out var version) || string.IsNullOrWhiteSpace(version))
                {
                    return;
                }

                var cache = _settings.Persisted.ThemeMigrationVersionCache
                    ?? new Dictionary<string, ThemeMigrationCacheEntry>(StringComparer.OrdinalIgnoreCase);

                cache[themePath] = new ThemeMigrationCacheEntry
                {
                    ThemeName = GetThemeDisplayName(themePath),
                    ThemePath = themePath,
                    MigratedThemeVersion = version,
                    MigratedAtUtc = DateTime.UtcNow
                };

                _settings.Persisted.ThemeMigrationVersionCache = cache;
                _saveSettings?.Invoke();
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to update theme migration version cache.");
            }
        }

        private static string GetThemeDisplayName(string themePath)
        {
            if (string.IsNullOrWhiteSpace(themePath))
            {
                return "Theme";
            }

            try
            {
                var themeDir = new DirectoryInfo(themePath);
                var parent = themeDir.Parent?.Name;
                if (string.Equals(parent, "Desktop", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(parent, "Fullscreen", StringComparison.OrdinalIgnoreCase))
                {
                    return $"{parent}/{themeDir.Name}";
                }
                return themeDir.Name;
            }
            catch
            {
                return Path.GetFileName(themePath);
            }
        }

        /// <summary>
        /// Performs the migration: backs up modified files and applies replacements.
        /// </summary>
        private MigrationResult PerformMigration(string themePath, string backupPath, MigrationMode mode, CustomMigrationSelection customSelection)
        {
            var backedUpFiles = new List<string>();
            int filesProcessed = 0;
            int replacementsMade = 0;
            int controlReplacementsMade = 0;
            int bindingReplacementsMade = 0;
            int filesSkipped = 0;

            var themeDir = new DirectoryInfo(themePath);

            _logger.Info($"Scanning theme for migration references: {themePath}");

            // First pass: find files that need replacement and back them up
            foreach (var file in themeDir.GetFiles("*.*", SearchOption.AllDirectories))
            {
                if (file.FullName.StartsWith(backupPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string extension = file.Extension.ToLowerInvariant();
                if (!IsProcessableFile(extension))
                {
                    continue;
                }

                var (needsReplacement, replacementCount) = CheckIfNeedsReplacement(file, mode, customSelection);
                if (!needsReplacement)
                {
                    filesSkipped++;
                    _logger.Debug($"Skipped file (no migration references): {GetRelativePath(file.FullName, themePath)}");
                    continue;
                }

                // Back up the file
                BackupFile(file, themePath, backupPath, backedUpFiles);
                _logger.Info($"Backing up file with {replacementCount} references: {GetRelativePath(file.FullName, themePath)}");

                // Apply replacements
                var (processed, textCount, ctrlCount, bindCount) = ProcessFile(file, mode, customSelection);
                if (processed)
                {
                    filesProcessed++;
                    replacementsMade += textCount;
                    controlReplacementsMade += ctrlCount;
                    bindingReplacementsMade += bindCount;
                }
            }

            // Only create backup folder and manifest if we actually backed up files
            if (backedUpFiles.Count > 0)
            {
                WriteManifest(backupPath, backedUpFiles, mode, customSelection);
            }
            else
            {
                _logger.Info("No files contained references requiring migration - no backup created");
            }

            _logger.Info($"Migration complete: {filesProcessed} files modified, {filesSkipped} files skipped, {replacementsMade} text replacements, {controlReplacementsMade} control replacements, {bindingReplacementsMade} binding replacements");

            return new MigrationResult
            {
                FilesBackedUp = backedUpFiles.Count,
                FilesProcessed = filesProcessed,
                ReplacementsMade = replacementsMade,
                ControlReplacementsMade = controlReplacementsMade,
                BindingReplacementsMade = bindingReplacementsMade
            };
        }

        /// <summary>
        /// Gets the relative path from themePath to fullPath.
        /// </summary>
        private string GetRelativePath(string fullPath, string themePath)
        {
            if (fullPath.StartsWith(themePath, StringComparison.OrdinalIgnoreCase))
            {
                return fullPath.Substring(themePath.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            return fullPath;
        }

        /// <summary>
        /// Checks if a file contains migration references for the selected mode.
        /// </summary>
        private (bool needsReplacement, int count) CheckIfNeedsReplacement(
            FileInfo file,
            MigrationMode mode,
            CustomMigrationSelection customSelection)
        {
            try
            {
                var content = File.ReadAllText(file.FullName, Encoding.UTF8);

                int fullscreenHelperCount = CountOccurrences(content, "SuccessStoryFullscreenHelper");
                int pluginIdCount = CountOccurrences(content, "playnite-successstory-plugin");
                int helperCount = CountOccurrences(content, "SSHelper");
                int successStoryCount = CountOccurrences(content, "SuccessStory");
                int iconCount = CountOccurrences(content, "\uE820");  // SuccessStory trophy icon (U+E820)
                int iconEntityCount = CountOccurrences(content, "&#xE820;");
                int totalCount = fullscreenHelperCount + pluginIdCount + helperCount + successStoryCount + iconCount + iconEntityCount;

                foreach (var mapping in GetSelectedControlMappings(mode, customSelection))
                {
                    totalCount += CountStandaloneControlNameOccurrences(content, mapping.Key);
                    totalCount += CountOccurrences(content, $"SuccessStory_{mapping.Key}");
                    totalCount += CountOccurrences(content, $"PlayniteAchievements_{mapping.Key}");
                }

                if (ShouldModernizeBindings(mode, customSelection))
                {
                    foreach (var bindingPath in ControlMappings.LegacyToModernBindingPaths.Keys)
                    {
                        totalCount += CountOccurrences(content, $"LegacyData.{bindingPath}");
                    }
                }

                return (totalCount > 0, totalCount);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, $"Could not check file for replacement: {file.FullName}");
                return (false, 0);
            }
        }

        /// <summary>
        /// Backs up a single file, preserving directory structure.
        /// </summary>
        private void BackupFile(FileInfo file, string themePath, string backupPath, List<string> backedUpFiles)
        {
            string relativePath = file.FullName.Substring(themePath.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string destFile = Path.Combine(backupPath, relativePath);
            string destDir = Path.GetDirectoryName(destFile);

            if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
            {
                Directory.CreateDirectory(destDir);
            }

            file.CopyTo(destFile, overwrite: false);
            backedUpFiles.Add(relativePath);
        }

        /// <summary>
        /// Writes the backup manifest file with migration metadata.
        /// </summary>
        private void WriteManifest(string backupPath, List<string> backedUpFiles, MigrationMode mode, CustomMigrationSelection customSelection)
        {
            string manifestPath = Path.Combine(backupPath, ManifestFileName);

            var lines = new List<string>
            {
                $"# Mode: {mode}",
                $"# Date: {DateTime.UtcNow:O}"
            };

            if (mode == MigrationMode.Custom)
            {
                var modernControls = GetSelectedControlMappings(mode, customSelection)
                    .Select(mapping => mapping.Key)
                    .ToList();
                lines.Add($"# Modern controls: {(modernControls.Count > 0 ? string.Join(", ", modernControls) : "None")}");
                lines.Add($"# Modern bindings: {ShouldModernizeBindings(mode, customSelection)}");
            }

            lines.Add(string.Empty);
            lines.AddRange(backedUpFiles);

            File.WriteAllLines(manifestPath, lines);
            _logger.Info($"Wrote manifest with {backedUpFiles.Count} entries to: {manifestPath} (Mode: {mode})");
        }

        /// <summary>
        /// Reads the backup manifest file.
        /// </summary>
        private List<string> ReadManifest(string backupPath)
        {
            string manifestPath = Path.Combine(backupPath, ManifestFileName);
            if (!File.Exists(manifestPath))
            {
                _logger.Warn($"Manifest file not found: {manifestPath}");
                return new List<string>();
            }

            var files = File.ReadAllLines(manifestPath).Where(f => !string.IsNullOrWhiteSpace(f)).ToList();
            _logger.Info($"Read manifest with {files.Count} entries from: {manifestPath}");
            return files;
        }

        /// <summary>
        /// Processes a single file, performing replacements.
        /// </summary>
        private (bool processed, int textReplacements, int controlReplacements, int bindingReplacements) ProcessFile(
            FileInfo file,
            MigrationMode mode,
            CustomMigrationSelection customSelection)
        {
            string content;
            try
            {
                content = File.ReadAllText(file.FullName, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, $"Failed to read file: {file.FullName}");
                return (false, 0, 0, 0);
            }

            string originalContent = content;
            int textReplacements = 0;
            int controlReplacements = 0;
            int bindingReplacements = 0;
            bool isYamlFile = file.Extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase);

            // For YAML files, preserve URLs to avoid breaking external references
            if (isYamlFile)
            {
                content = ProcessYamlFile(content, originalContent, ref textReplacements);
            }
            else
            {
                content = ProcessStandardFile(content, originalContent, ref textReplacements);
            }

            // Apply selected modernization replacements if enabled
            if (ShouldApplyModernization(mode, customSelection))
            {
                content = ApplyModernizationReplacements(content, mode, customSelection, ref controlReplacements, ref bindingReplacements);
            }

            if (content != originalContent)
            {
                try
                {
                    File.WriteAllText(file.FullName, content, Encoding.UTF8);
                    _logger.Debug($"Replaced {textReplacements} text, {controlReplacements} control, {bindingReplacements} binding occurrences in: {file.Name}");
                    return (true, textReplacements, controlReplacements, bindingReplacements);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, $"Failed to write file: {file.FullName}");
                    return (false, 0, 0, 0);
                }
            }

            return (false, 0, 0, 0);
        }

        /// <summary>
        /// Processes YAML files, preserving URLs that should not be modified.
        /// </summary>
        private string ProcessYamlFile(string content, string originalContent, ref int replacements)
        {
            string result = content;
            var urlPrefixes = new[] { "https://", "http://", "Url:", "PackageUrl:", "SourceUrl:", "GitHub:" };

            // Find and temporarily replace URLs to prevent modification
            var placeholders = new Dictionary<string, string>();
            int placeholderIndex = 0;

            foreach (var line in result.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmedLine = line.TrimStart();
                if (string.IsNullOrWhiteSpace(trimmedLine))
                {
                    continue;
                }

                // Check if this line contains a URL that should be preserved
                bool isUrlLine = false;
                foreach (var prefix in urlPrefixes)
                {
                    if (trimmedLine.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                        trimmedLine.Contains(prefix))
                    {
                        isUrlLine = true;
                        break;
                    }
                }

                if (isUrlLine && line.IndexOf("SuccessStory", StringComparison.Ordinal) >= 0)
                {
                    string placeholder = $"__URL_PLACEHOLDER_{placeholderIndex}__";
                    placeholders[placeholder] = line;
                    result = result.Replace(line, placeholder);
                    placeholderIndex++;
                }
            }

            // Apply replacements to content with URLs protected
            result = ApplyReplacements(result, originalContent, ref replacements);

            // Restore URLs
            foreach (var kvp in placeholders)
            {
                result = result.Replace(kvp.Key, kvp.Value);
            }

            return result;
        }

        /// <summary>
        /// Processes standard files (XAML, C#) with all replacements applied.
        /// </summary>
        private string ProcessStandardFile(string content, string originalContent, ref int replacements)
        {
            return ApplyReplacements(content, originalContent, ref replacements);
        }

        /// <summary>
        /// Applies all SuccessStory to PlayniteAchievements replacements.
        /// </summary>
        private string ApplyReplacements(string content, string originalContent, ref int replacements)
        {
            string result = content;

            // Replace SuccessStoryFullscreenHelper first (most specific - fullscreen installation checks).
            // Uses PluginExtensionId because Playnite's ExtensionsStatusBinder resolves
            // [id].IsInstalled by matching Description.Id from extension.yaml, not the SourceName.
            result = result.Replace("SuccessStoryFullscreenHelper", PluginExtensionId);
            replacements += CountOccurrences(originalContent, "SuccessStoryFullscreenHelper");

            // Replace playnite-successstory-plugin second (desktop installation checks).
            // Same reason: Description.Id must match for IsInstalled to return true.
            result = result.Replace("playnite-successstory-plugin", PluginExtensionId);
            replacements += CountOccurrences(originalContent, "playnite-successstory-plugin");

            // Replace SSHelper third (class references).
            // Uses ThemeSourceName because SSHelper appears as a control-name prefix,
            // not as an extension ID.
            result = result.Replace("SSHelper", ThemeSourceName);
            replacements += CountOccurrences(originalContent, "SSHelper");

            // Then replace SuccessStory (most general - catches remaining control-name prefixes
            // such as SuccessStory_PluginButton → PlayniteAchievements_PluginButton).
            // Uses ThemeSourceName because control names are looked up via the registered SourceName.
            result = result.Replace("SuccessStory", ThemeSourceName);
            replacements += CountOccurrences(originalContent, "SuccessStory");

            // Fix style key names to match plugin expectations
            // Plugin looks for "GameAchievementsWindow" but themes have "GameAchievementsWindowStyle"
            result = result.Replace("GameAchievementsWindowStyle", "GameAchievementsWindow");
            replacements += CountOccurrences(originalContent, "GameAchievementsWindowStyle");
            result = result.Replace("AchievementsWindowStyle", "AchievementsWindow");
            replacements += CountOccurrences(originalContent, "AchievementsWindowStyle");

            // Convert DataContext bindings to use PluginSettings with our exposed properties
            // {Binding SelectedGame.DisplayBackgroundImageObject} -> {PluginSettings Plugin=PlayniteAchievements, Path=SelectedGameBackgroundPath}
            // {Binding SelectedGame.CoverImageObjectCached} -> {PluginSettings Plugin=PlayniteAchievements, Path=SelectedGameCoverPath}
            result = result.Replace("{Binding SelectedGame.DisplayBackgroundImageObject}", "{PluginSettings Plugin=PlayniteAchievements, Path=SelectedGameBackgroundPath}");
            replacements += CountOccurrences(originalContent, "{Binding SelectedGame.DisplayBackgroundImageObject}");

            result = result.Replace("{Binding SelectedGame.CoverImageObjectCached}", "{PluginSettings Plugin=PlayniteAchievements, Path=SelectedGameCoverPath}");
            replacements += CountOccurrences(originalContent, "{Binding SelectedGame.CoverImageObjectCached}");

            // This path targets Playnite's Game type, which we cannot extend with a
            // DisplayBackgroundImageObject property.
            result = result.Replace("{Binding Game.DisplayBackgroundImageObject}", "{PluginSettings Plugin=PlayniteAchievements, Path=SelectedGameBackgroundPath}");
            replacements += CountOccurrences(originalContent, "{Binding Game.DisplayBackgroundImageObject}");

            // Convert SuccessStory trophy icon (U+E820) to PlayniteAchievements trophy icon (U+EDD7)
            // Note: The actual character in theme files is U+E820 (UTF-8: EE A0 A0), not U+F820
            var trophySource = "\uE820";
            var trophyTarget = "\uEDD7";
            if (result.Contains(trophySource))
            {
                result = result.Replace(trophySource, trophyTarget);
                replacements += CountOccurrences(originalContent, trophySource);
            }

            // Also handle XML entity form just in case
            result = result.Replace("&#xE820;", "&#xEDD7;");
            replacements += CountOccurrences(originalContent, "&#xE820;");

            return result;
        }

        /// <summary>
        /// Applies modernization replacements: selected control element names and binding paths.
        /// </summary>
        private string ApplyModernizationReplacements(
            string content,
            MigrationMode mode,
            CustomMigrationSelection customSelection,
            ref int controlReplacements,
            ref int bindingReplacements)
        {
            string result = content;

            // Replace only the PlayniteAchievements/SuccessStory host names or exact standalone placeholders.
            // Do not touch unrelated plugin hosts like ScreenshotsVisualizer_PluginButton.
            foreach (var mapping in GetSelectedControlMappings(mode, customSelection))
            {
                controlReplacements += ReplacePrefixedControlName(ref result, "PlayniteAchievements", mapping.Key, mapping.Value);
                controlReplacements += ReplacePrefixedControlName(ref result, "SuccessStory", mapping.Key, mapping.Value);
                controlReplacements += ReplaceStandaloneControlName(ref result, mapping.Key, mapping.Value);
            }

            // Replace LegacyData binding paths with Theme binding paths
            // These appear in XAML as {Binding LegacyData.HasData} etc.
            if (ShouldModernizeBindings(mode, customSelection))
            {
                foreach (var mapping in ControlMappings.LegacyToModernBindingPaths)
                {
                    string legacyBinding = $"LegacyData.{mapping.Key}";
                    string modernBinding = $"Theme.{mapping.Value}";
                    int replacements = CountOccurrences(result, legacyBinding);
                    if (replacements > 0)
                    {
                        result = result.Replace(legacyBinding, modernBinding);
                        bindingReplacements += replacements;
                    }
                }
            }

            return result;
        }

        private static bool ShouldApplyModernization(MigrationMode mode, CustomMigrationSelection customSelection)
        {
            return GetSelectedControlMappings(mode, customSelection).Any() ||
                   ShouldModernizeBindings(mode, customSelection);
        }

        private static bool ShouldModernizeBindings(MigrationMode mode, CustomMigrationSelection customSelection)
        {
            return mode == MigrationMode.Full ||
                   (mode == MigrationMode.Custom && customSelection?.ModernizeBindings == true);
        }

        private static IEnumerable<KeyValuePair<string, string>> GetSelectedControlMappings(
            MigrationMode mode,
            CustomMigrationSelection customSelection)
        {
            if (mode == MigrationMode.Full)
            {
                return ControlMappings.LegacyToModernControlNames;
            }

            if (mode != MigrationMode.Custom || customSelection == null)
            {
                return Enumerable.Empty<KeyValuePair<string, string>>();
            }

            return ControlMappings.LegacyToModernControlNames
                .Where(mapping => customSelection.ShouldModernizeControl(mapping.Key));
        }

        private static int ReplacePrefixedControlName(ref string content, string prefix, string legacyName, string modernName)
        {
            string legacyToken = $"{prefix}_{legacyName}";
            string modernToken = $"{prefix}_{modernName}";
            int replacements = CountOccurrences(content, legacyToken);
            if (replacements > 0)
            {
                content = content.Replace(legacyToken, modernToken);
            }

            return replacements;
        }

        private static int ReplaceStandaloneControlName(ref string content, string legacyName, string modernName)
        {
            string pattern = GetStandaloneControlNamePattern(legacyName);
            var matches = Regex.Matches(content, pattern);
            if (matches.Count > 0)
            {
                content = Regex.Replace(content, pattern, $"_{modernName}");
            }

            return matches.Count;
        }

        private static int CountStandaloneControlNameOccurrences(string content, string controlName)
        {
            return Regex.Matches(content, GetStandaloneControlNamePattern(controlName)).Count;
        }

        private static string GetStandaloneControlNamePattern(string controlName)
        {
            return $@"(?<![A-Za-z0-9])_{Regex.Escape(controlName)}(?![A-Za-z0-9])";
        }

        /// <summary>
        /// Builds a success message based on the migration result and mode.
        /// </summary>
        private string BuildSuccessMessage(MigrationResult result, MigrationMode mode)
        {
            if (mode == MigrationMode.Limited)
            {
                return $"Theme migrated successfully (Limited). {result.FilesBackedUp} files backed up, {result.FilesProcessed} files modified.";
            }

            if (mode == MigrationMode.Full)
            {
                return $"Theme migrated successfully (Full). {result.FilesBackedUp} files backed up, {result.FilesProcessed} files modified, {result.ControlReplacementsMade} control replacements, {result.BindingReplacementsMade} binding replacements.";
            }

            return $"Theme migrated successfully (Custom). {result.FilesBackedUp} files backed up, {result.FilesProcessed} files modified, {result.ControlReplacementsMade} control replacements, {result.BindingReplacementsMade} binding replacements.";
        }

        /// <summary>
        /// Determines if a file type should be processed for replacements.
        /// Skips binary files that should not be read as text.
        /// </summary>
        private bool IsProcessableFile(string extension)
        {
            // Skip known binary extensions
            if (BinaryExtensions.Contains(extension))
            {
                return false;
            }

            // Process all other files - content matching will determine if changes are needed
            return true;
        }

        /// <summary>
        /// Counts non-overlapping occurrences of a substring in text.
        /// </summary>
        private static int CountOccurrences(string text, string pattern)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(pattern))
            {
                return 0;
            }

            int count = 0;
            int index = 0;
            int patternLength = pattern.Length;

            while ((index = text.IndexOf(pattern, index, StringComparison.Ordinal)) != -1)
            {
                count++;
                index += patternLength;
            }

            return count;
        }

        /// <summary>
        /// Reverts a theme to its original state from backup.
        /// </summary>
        /// <param name="themePath">Full path to the theme directory.</param>
        /// <returns>Migration result with status and details.</returns>
        public async Task<MigrationResult> RevertThemeAsync(string themePath)
        {
            if (string.IsNullOrWhiteSpace(themePath))
            {
                return new MigrationResult
                {
                    Success = false,
                    Message = "Theme path cannot be empty."
                };
            }

            if (!Directory.Exists(themePath))
            {
                return new MigrationResult
                {
                    Success = false,
                    Message = $"Theme directory does not exist: {themePath}"
                };
            }

            var backupPath = Path.Combine(themePath, BackupFolderName);

            if (!Directory.Exists(backupPath))
            {
                return new MigrationResult
                {
                    Success = false,
                    Message = $"No backup folder found at: {BackupFolderName}"
                };
            }

            _logger.Info($"Starting theme revert for: {themePath}");

            try
            {
                var filesRestored = await Task.Run(() =>
                {
                    return RestoreFromBackup(themePath, backupPath);
                });

                _logger.Info($"Theme revert completed successfully for: {themePath}");

                return new MigrationResult
                {
                    Success = true,
                    Message = filesRestored == 0
                        ? "No files needed restoring (backup was empty). Backup folder deleted."
                        : $"Theme reverted successfully. {filesRestored} files restored. Backup folder deleted."
                };
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Failed to revert theme at: {themePath}");
                return new MigrationResult
                {
                    Success = false,
                    Message = $"Revert failed: {ex.Message}"
                };
            }
        }

        /// <summary>
        /// Restores theme files from the backup directory using the manifest.
        /// </summary>
        private int RestoreFromBackup(string themePath, string backupPath)
        {
            _logger.Info($"Restoring from backup: {backupPath}");

            var backedUpFiles = ReadManifest(backupPath);
            int filesRestored = 0;

            foreach (var relativePath in backedUpFiles)
            {
                string backupFile = Path.Combine(backupPath, relativePath);
                string themeFile = Path.Combine(themePath, relativePath);

                if (!File.Exists(backupFile))
                {
                    _logger.Warn($"Backup file not found: {backupFile}");
                    continue;
                }

                try
                {
                    // Ensure target directory exists
                    string targetDir = Path.GetDirectoryName(themeFile);
                    if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
                    {
                        Directory.CreateDirectory(targetDir);
                    }

                    File.Copy(backupFile, themeFile, overwrite: true);
                    filesRestored++;
                    _logger.Debug($"Restored file: {relativePath}");
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, $"Failed to restore file: {relativePath}");
                }
            }

            // Delete the backup folder
            try
            {
                Directory.Delete(backupPath, recursive: true);
                _logger.Info($"Deleted backup folder: {backupPath}");
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, $"Could not delete backup folder: {backupPath}");
            }

            _logger.Info($"Restore completed: {filesRestored} files restored");
            return filesRestored;
        }

        /// <summary>
        /// Checks if a theme has a backup folder.
        /// </summary>
        /// <param name="themePath">Full path to the theme directory.</param>
        /// <returns>True if backup folder exists.</returns>
        public bool HasBackup(string themePath)
        {
            if (string.IsNullOrWhiteSpace(themePath))
            {
                return false;
            }

            var backupPath = Path.Combine(themePath, BackupFolderName);
            return Directory.Exists(backupPath);
        }
    }
}

