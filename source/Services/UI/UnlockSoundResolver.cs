using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Playnite.SDK;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Services.UI
{
    /// <summary>Where a tier's sound came from, for the settings table and the log.</summary>
    public enum UnlockSoundSource
    {
        None,
        Custom,
        Theme,
        Default,
    }

    public sealed class ResolvedUnlockSound
    {
        public ResolvedUnlockSound(UnlockSoundTier tier, UnlockSoundSource source, string path)
        {
            Tier = tier;
            Source = source;
            Path = path;
        }

        public UnlockSoundTier Tier { get; }
        public UnlockSoundSource Source { get; }

        /// <summary>Absolute file path; null when <see cref="Source"/> is <see cref="UnlockSoundSource.None"/>.</summary>
        public string Path { get; }
    }

    /// <summary>
    /// One theme-supplied sound file found for a tier, and which Playnite mode's active theme
    /// shipped it. Both modes are reported so the settings page can play either without the user
    /// restarting Playnite into the other mode.
    /// </summary>
    public sealed class ThemeUnlockSoundCandidate
    {
        public ThemeUnlockSoundCandidate(UnlockSoundTier tier, string modeName, string path)
        {
            Tier = tier;
            ModeName = modeName;
            Path = path;
        }

        public UnlockSoundTier Tier { get; }

        /// <summary><see cref="UnlockSoundResolver.DesktopModeName"/> or <see cref="UnlockSoundResolver.FullscreenModeName"/>.</summary>
        public string ModeName { get; }

        public string Path { get; }
    }

    /// <summary>
    /// Picks the sound file for a tier: the user's own path, then the active theme, then the
    /// bundled pack. Theme directories are supplied by the caller (memoized by the template
    /// resolver); file existence is a live check on every resolve so a file dropped into a theme
    /// mid-session is picked up without a restart.
    /// </summary>
    public sealed class UnlockSoundResolver
    {
        /// <summary>Playnite's theme mode folder names, which are also the labels the modes carry.</summary>
        public const string DesktopModeName = "Desktop";

        public const string FullscreenModeName = "Fullscreen";
        /// <summary>The layout themes should use: <c>PlayniteAchievements\Sounds\{tier}.{ext}</c>.</summary>
        public const string ThemeSoundsRelativeDirectory = "PlayniteAchievements\\Sounds";

        /// <summary>The UniPlaySong-era layout still accepted: <c>audio\Achievements\{tier}.{ext}</c>.</summary>
        public const string LegacyThemeSoundsRelativeDirectory = "audio\\Achievements";

        /// <summary>Probed in this order; everything Media Foundation decodes without extra codecs.</summary>
        public static readonly string[] ProbedExtensions = { ".wav", ".mp3", ".flac" };

        private const string SkippedExtension = ".ogg";
        private const string BundledExtension = ".mp3";

        private static readonly string[] ThemeLayouts =
        {
            ThemeSoundsRelativeDirectory,
            LegacyThemeSoundsRelativeDirectory,
        };

        private readonly Func<UnlockSoundSettings> _getSettings;
        private readonly Func<IReadOnlyList<string>> _getActiveThemeDirectories;
        private readonly string _bundledSoundsDirectory;
        private readonly ILogger _logger;
        private readonly Func<bool> _getAllowThemeSounds;
        private readonly Func<string, IReadOnlyList<string>> _getThemeDirectoriesForMode;
        private readonly Func<string> _getRunningModeName;
        private readonly HashSet<string> _reportedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <param name="getAllowThemeSounds">
        /// Whether the theme step is in the chain. Null means it is, which is the setting's default
        /// and keeps a caller that does not care about the switch working unchanged.
        /// </param>
        /// <param name="getThemeDirectoriesForMode">
        /// Theme directories for an explicitly named mode, for <see cref="FindThemeCandidates"/>.
        /// Null leaves candidate discovery with only the running mode's directories.
        /// </param>
        /// <param name="getRunningModeName">
        /// Which mode Playnite is running, used only to order the candidate list. Null means
        /// Desktop.
        /// </param>
        public UnlockSoundResolver(
            Func<UnlockSoundSettings> getSettings,
            Func<IReadOnlyList<string>> getActiveThemeDirectories,
            string bundledSoundsDirectory,
            ILogger logger,
            Func<bool> getAllowThemeSounds = null,
            Func<string, IReadOnlyList<string>> getThemeDirectoriesForMode = null,
            Func<string> getRunningModeName = null)
        {
            _getSettings = getSettings;
            _getActiveThemeDirectories = getActiveThemeDirectories;
            _bundledSoundsDirectory = bundledSoundsDirectory;
            _logger = logger;
            _getAllowThemeSounds = getAllowThemeSounds;
            _getThemeDirectoriesForMode = getThemeDirectoriesForMode;
            _getRunningModeName = getRunningModeName;
        }

        /// <summary>The bundled pack's directory next to the plugin assembly.</summary>
        public static string GetBundledSoundsDirectory(string pluginInstallDirectory)
        {
            return string.IsNullOrWhiteSpace(pluginInstallDirectory)
                ? null
                : Path.Combine(pluginInstallDirectory, "Resources", "Sounds");
        }

        public static string BuildOpenFileDialogFilter()
        {
            var patterns = string.Join(";", ProbedExtensions.Select(ext => "*" + ext));
            return $"Audio Files ({patterns})|{patterns}";
        }

        public ResolvedUnlockSound Resolve(UnlockSoundTier tier)
        {
            return ResolveCustom(tier) ?? ResolveTheme(tier) ?? ResolveBundled(tier);
        }

        public IReadOnlyList<ResolvedUnlockSound> ResolveAll()
        {
            return UnlockSoundTierExtensions.All.Select(Resolve).ToList();
        }

        public void LogDiagnostics(string context = null)
        {
            if (_logger == null)
            {
                return;
            }

            var prefix = string.IsNullOrWhiteSpace(context) ? string.Empty : $"{context}: ";
            var themeDirectories = SafeThemeDirectories();
            _logger.Info(
                $"[UnlockSound] {prefix}themeDirectories={themeDirectories.Count} " +
                $"allowThemeSounds={SafeAllowThemeSounds()} " +
                $"bundled='{_bundledSoundsDirectory ?? "<null>"}' exists={Directory.Exists(_bundledSoundsDirectory ?? string.Empty)}");
            foreach (var resolved in ResolveAll())
            {
                _logger.Info(
                    $"[UnlockSound] {prefix}tier={resolved.Tier.ToFileBaseName()} source={resolved.Source} " +
                    $"path='{resolved.Path ?? "<none>"}'");
            }
        }

        private ResolvedUnlockSound ResolveCustom(UnlockSoundTier tier)
        {
            var path = _getSettings?.Invoke()?.GetPath(tier);
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            if (SafeFileExists(path))
            {
                return new ResolvedUnlockSound(tier, UnlockSoundSource.Custom, path);
            }

            ReportOnce(path, $"[UnlockSound] Custom sound for tier={tier.ToFileBaseName()} is missing: '{path}'; falling back.");
            return null;
        }

        private ResolvedUnlockSound ResolveTheme(UnlockSoundTier tier)
        {
            if (_getAllowThemeSounds != null && !SafeAllowThemeSounds())
            {
                return null;
            }

            var path = FindThemeSound(tier, SafeThemeDirectories());
            return path == null ? null : new ResolvedUnlockSound(tier, UnlockSoundSource.Theme, path);
        }

        /// <summary>
        /// The theme sound for a tier in the first of <paramref name="directories"/> that has one,
        /// or null. Logs an unusable <c>.ogg</c> sibling once, so a theme author who shipped the
        /// wrong format learns why the tier fell through.
        /// </summary>
        private string FindThemeSound(UnlockSoundTier tier, IReadOnlyList<string> directories)
        {
            var name = tier.ToFileBaseName();
            foreach (var directory in directories ?? Array.Empty<string>())
            {
                foreach (var layout in ThemeLayouts)
                {
                    var layoutDirectory = Path.Combine(directory, layout);
                    foreach (var extension in ProbedExtensions)
                    {
                        var candidate = Path.Combine(layoutDirectory, name + extension);
                        if (SafeFileExists(candidate))
                        {
                            return candidate;
                        }
                    }

                    var skipped = Path.Combine(layoutDirectory, name + SkippedExtension);
                    if (SafeFileExists(skipped))
                    {
                        ReportOnce(
                            skipped,
                            $"[UnlockSound] Skipping '{skipped}': {SkippedExtension} is not supported " +
                            $"(use {string.Join(", ", ProbedExtensions)}).");
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Every theme-supplied sound for a tier, one per Playnite mode that has one, running mode
        /// first. Reported whether or not <see cref="PersistedSettings.AllowThemeUnlockSounds"/> is
        /// on and whether or not the tier currently resolves to the theme: this answers "what does
        /// each theme provide", which is what a theme author testing both modes needs, and only one
        /// mode's theme is ever the one <see cref="Resolve"/> reads.
        /// </summary>
        public IReadOnlyList<ThemeUnlockSoundCandidate> FindThemeCandidates(UnlockSoundTier tier)
        {
            var candidates = new List<ThemeUnlockSoundCandidate>();
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var modeName in ModeNamesRunningModeFirst())
            {
                var path = FindThemeSound(tier, SafeThemeDirectories(modeName));
                if (path != null && seenPaths.Add(path))
                {
                    candidates.Add(new ThemeUnlockSoundCandidate(tier, modeName, path));
                }
            }

            return candidates;
        }

        /// <summary>
        /// Both mode names, the running one first, so a candidate list reads in the order the user
        /// cares about and a file shared by both modes is attributed to the running one.
        /// </summary>
        private IReadOnlyList<string> ModeNamesRunningModeFirst()
        {
            var running = SafeRunningModeName();
            return string.Equals(running, FullscreenModeName, StringComparison.OrdinalIgnoreCase)
                ? new[] { FullscreenModeName, DesktopModeName }
                : new[] { DesktopModeName, FullscreenModeName };
        }

        /// <summary>
        /// The mode Playnite is running, as the host reports it. Asked rather than inferred from
        /// which directories the active-mode delegate returns: two modes can be configured with
        /// theme folders that overlap, and a directory comparison would then name the wrong mode.
        /// </summary>
        private string SafeRunningModeName()
        {
            try
            {
                var mode = _getRunningModeName?.Invoke();
                return string.Equals(mode, FullscreenModeName, StringComparison.OrdinalIgnoreCase)
                    ? FullscreenModeName
                    : DesktopModeName;
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[UnlockSound] The running Playnite mode could not be read.");
                return DesktopModeName;
            }
        }

        private bool SafeAllowThemeSounds()
        {
            try
            {
                return _getAllowThemeSounds?.Invoke() ?? true;
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[UnlockSound] The theme-sound switch could not be read; allowing theme sounds.");
                return true;
            }
        }

        private ResolvedUnlockSound ResolveBundled(UnlockSoundTier tier)
        {
            if (!string.IsNullOrWhiteSpace(_bundledSoundsDirectory))
            {
                var candidate = Path.Combine(_bundledSoundsDirectory, tier.ToFileBaseName() + BundledExtension);
                if (SafeFileExists(candidate))
                {
                    return new ResolvedUnlockSound(tier, UnlockSoundSource.Default, candidate);
                }

                ReportOnce(candidate, $"[UnlockSound] Bundled sound missing: '{candidate}'.", warn: true);
            }

            return new ResolvedUnlockSound(tier, UnlockSoundSource.None, null);
        }

        private IReadOnlyList<string> SafeThemeDirectories()
        {
            try
            {
                return _getActiveThemeDirectories?.Invoke() ?? Array.Empty<string>();
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[UnlockSound] Theme directories could not be resolved.");
                return Array.Empty<string>();
            }
        }

        /// <summary>
        /// Theme directories for one named mode. Falls back to the running mode's directories when
        /// no per-mode delegate was supplied, so candidate discovery degrades to what the old
        /// single-mode wiring could see rather than to nothing.
        /// </summary>
        private IReadOnlyList<string> SafeThemeDirectories(string modeName)
        {
            if (_getThemeDirectoriesForMode == null)
            {
                return SafeThemeDirectories();
            }

            try
            {
                return _getThemeDirectoriesForMode(modeName) ?? Array.Empty<string>();
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"[UnlockSound] Theme directories for mode '{modeName}' could not be resolved.");
                return Array.Empty<string>();
            }
        }

        private static bool SafeFileExists(string path)
        {
            try
            {
                return File.Exists(path);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void ReportOnce(string path, string message, bool warn = false)
        {
            lock (_reportedPaths)
            {
                if (!_reportedPaths.Add(path))
                {
                    return;
                }
            }

            if (warn)
            {
                _logger?.Warn(message);
            }
            else
            {
                _logger?.Info(message);
            }
        }
    }
}
