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
    /// Picks the sound file for a tier: the user's own path, then the active theme, then the
    /// bundled pack. Theme directories are supplied by the caller (memoized by the template
    /// resolver); file existence is a live check on every resolve so a file dropped into a theme
    /// mid-session is picked up without a restart.
    /// </summary>
    public sealed class UnlockSoundResolver
    {
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
        private readonly HashSet<string> _reportedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public UnlockSoundResolver(
            Func<UnlockSoundSettings> getSettings,
            Func<IReadOnlyList<string>> getActiveThemeDirectories,
            string bundledSoundsDirectory,
            ILogger logger)
        {
            _getSettings = getSettings;
            _getActiveThemeDirectories = getActiveThemeDirectories;
            _bundledSoundsDirectory = bundledSoundsDirectory;
            _logger = logger;
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
            var name = tier.ToFileBaseName();
            foreach (var directory in SafeThemeDirectories())
            {
                foreach (var layout in ThemeLayouts)
                {
                    var layoutDirectory = Path.Combine(directory, layout);
                    foreach (var extension in ProbedExtensions)
                    {
                        var candidate = Path.Combine(layoutDirectory, name + extension);
                        if (SafeFileExists(candidate))
                        {
                            return new ResolvedUnlockSound(tier, UnlockSoundSource.Theme, candidate);
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
