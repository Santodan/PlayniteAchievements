using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.ViewModels
{
    /// <summary>
    /// Resolves the achievement artwork a notification shows, so no template has to know how an
    /// image is sourced. Two forms come out of here: the display source (the decorated string the
    /// image service decodes, carrying the grayscale and cache-bust markers) and the bitmap that
    /// string decodes to. Templates bind the bitmap; the string stays internal apart from the
    /// plain path the view model publishes for the ray-burst silhouette and legacy bindings.
    /// </summary>
    internal static class ToastImageResolver
    {
        /// <summary>
        /// Decode size for notification artwork, matched to the icon prime in
        /// ToastNotificationService: the image cache keys on the requested size as well as the
        /// source string, so a different value here would decode the same file a second time
        /// rather than reuse the primed bitmap.
        /// </summary>
        public const int IconDecodePixel = 160;

        /// <summary>
        /// The display source for a notification's achievement icon, honoring the achievement
        /// visibility settings. An unlocked achievement is never a spoiler, so masking only ever
        /// applies to a progress notification, whose achievement is still locked: a hidden one
        /// takes the hidden cover, and any locked one takes the locked cover when locked icons are
        /// hidden. Otherwise this is the real artwork -- the provider's locked art when it ships
        /// distinct art, else the unlocked art grayscaled.
        /// </summary>
        public static string ResolveIconDisplaySource(
            AchievementUnlockedEventArgs args, PersistedSettings settings)
        {
            if (args == null)
            {
                return AchievementIconResolver.GetDefaultIcon();
            }

            var locked = args.IsProgressUpdate;
            var hidden = locked && args.IsHidden;

            return AchievementIconResolver.ResolveRowDisplayIcon(
                isIconHidden: hidden && settings?.ShowHiddenIcon != true,
                isLockedIconHidden: locked && settings?.ShowLockedIcon != true,
                unlocked: !locked,
                unlockedIconPath: args.IconPath,
                lockedIconPath: args.LockedIconPath);
        }

        /// <summary>
        /// Decodes a display source through the shared image cache, which strips the cache-bust
        /// token, applies the grayscale marker, and returns a frozen bitmap (so the result may
        /// cross threads freely). Null when the plugin has no image service yet, when the source
        /// is blank, or when the decode fails -- callers render nothing rather than a broken box.
        /// </summary>
        public static async Task<ImageSource> LoadAsync(
            string displaySource, int decodePixel = IconDecodePixel)
        {
            var service = PlayniteAchievementsPlugin.Instance?.ImageService;
            if (service == null || string.IsNullOrWhiteSpace(displaySource))
            {
                return null;
            }

            try
            {
                return await service
                    .GetAsync(displaySource, decodePixel, CancellationToken.None)
                    .ConfigureAwait(true);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
