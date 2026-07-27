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

        public string CoverImage => _game?.CoverImage;

        public string BackgroundImage => _game?.BackgroundImage;

        public string CoverImageObjectCached => _coverImagePathProvider?.Invoke();

        // Legacy alias used by several fullscreen themes.
        public string CoverImageObject => _coverImagePathProvider?.Invoke();

        public string DisplayBackgroundImageObject => _backgroundImagePathProvider?.Invoke();
    }
}
