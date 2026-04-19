using Playnite.SDK.Models;
using System;

namespace PlayniteAchievements.Models
{
    /// <summary>
    /// Theme-facing selected game wrapper.
    /// Keeps the root PluginSettings DataContext intact while exposing
    /// a few Playnite-style properties fullscreen themes expect.
    /// </summary>
    public sealed class SelectedGameBindingContext
    {
        private readonly Game _game;
        private readonly Func<string> _coverImagePathProvider;
        private readonly Func<string> _backgroundImagePathProvider;

        public SelectedGameBindingContext(
            Game game,
            Func<string> coverImagePathProvider,
            Func<string> backgroundImagePathProvider)
        {
            _game = game;
            _coverImagePathProvider = coverImagePathProvider;
            _backgroundImagePathProvider = backgroundImagePathProvider;
        }

        public Game Game => _game;

        public Guid Id => _game?.Id ?? Guid.Empty;

        public string Name => _game?.Name;

        public string DisplayName => _game?.Name;

        public string CoverImage => NormalizeResolvedImagePath(_coverImagePathProvider?.Invoke());

        public string BackgroundImage => NormalizeResolvedImagePath(_backgroundImagePathProvider?.Invoke());

        public string CoverImageObjectCached => NormalizeResolvedImagePath(_coverImagePathProvider?.Invoke());

        // Legacy alias used by several fullscreen themes.
        public string CoverImageObject => NormalizeResolvedImagePath(_coverImagePathProvider?.Invoke());

        public string DisplayBackgroundImageObject => NormalizeResolvedImagePath(_backgroundImagePathProvider?.Invoke());

        private static string NormalizeResolvedImagePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var normalized = path.Trim();
            if (normalized.StartsWith("pack://", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return normalized;
            }

            return System.IO.File.Exists(normalized)
                ? normalized
                : null;
        }
    }
}
