using System;
using PlayniteAchievements.Providers.Local;

namespace PlayniteAchievements.Models.Settings
{
    /// <summary>
    /// Portable import/export format for Custom overlay style templates.
    /// This is intentionally user-editable JSON so presets can be shared online.
    /// </summary>
    public sealed class NotificationOverlayTemplateFile
    {
        public int SchemaVersion { get; set; } = 1;

        public string TemplateName { get; set; } = string.Empty;

        public DateTime ExportedAtUtc { get; set; } = DateTime.UtcNow;

        public LocalCustomOverlayStyleSlot CustomStyleSlot { get; set; } = new LocalCustomOverlayStyleSlot();

        public NotificationOverlayTransitionTemplate Transition { get; set; } = new NotificationOverlayTransitionTemplate();
    }

    public sealed class NotificationOverlayTransitionTemplate
    {
        public int DurationMilliseconds { get; set; } = 3400;

        public int FadeInMilliseconds { get; set; } = 180;

        public int FadeOutMilliseconds { get; set; } = 280;

        public int SoundLeadMilliseconds { get; set; }

        public double? Opacity { get; set; }

        public double? Scale { get; set; }

        public string OverlayPosition { get; set; } = LocalUnlockOverlayPosition.TopRight.ToString();

        public string TransitionStyle { get; set; } = LocalUnlockOverlayTransitionStyle.Fade.ToString();

        public string TemplateAnimationStyle { get; set; } = LocalCustomOverlayAnimationStyle.Standard.ToString();

        public int SlideDistance { get; set; } = 72;

        public double IconSize { get; set; }

        public double SecondaryIconSize { get; set; }

        public double IconCornerRadius { get; set; } = 10;

        public double SecondaryIconCornerRadius { get; set; } = 10;

        public string IconSource { get; set; } = LocalOverlayIconSource.AchievementIcon.ToString();

        public string SecondaryIconSource { get; set; } = LocalOverlayIconSource.None.ToString();

        public bool EnableGameCoverInOverlay { get; set; }

        public string GameCoverPosition { get; set; } = LocalOverlayCoverPosition.Right.ToString();

        public double GameCoverWidth { get; set; } = 80;

        public bool EnableGameBannerAsBackground { get; set; }

        public double GameBannerOpacity { get; set; } = 0.3;

        public int GameBannerBlurRadius { get; set; } = 8;

        public bool ShowIconRarityGlow { get; set; }

        public bool ShowSecondaryIconRarityGlow { get; set; }

        public string CustomCoverImagePath { get; set; } = string.Empty;

        public string CustomBannerImagePath { get; set; } = string.Empty;
    }
}
