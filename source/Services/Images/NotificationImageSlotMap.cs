using System.Collections.Generic;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Services.Images
{
    /// <summary>
    /// The authoritative mapping from each notification image slot to its path property on a
    /// <see cref="NotificationStyleSettings"/>. The image store, the packaging layers, and the
    /// settings editor all resolve slot paths through this map, so adding a slot cannot leave
    /// one consumer silently out of sync with the others.
    /// </summary>
    public static class NotificationImageSlotMap
    {
        /// <summary>Every slot, in a stable order for iteration.</summary>
        public static IReadOnlyList<NotificationImageSlot> Slots { get; } = new[]
        {
            NotificationImageSlot.Background,
            NotificationImageSlot.BadgeCommon,
            NotificationImageSlot.BadgeUncommon,
            NotificationImageSlot.BadgeRare,
            NotificationImageSlot.BadgeUltraRare,
            NotificationImageSlot.BadgeCompletion,
            NotificationImageSlot.FrameBadgeCommon,
            NotificationImageSlot.FrameBadgeUncommon,
            NotificationImageSlot.FrameBadgeRare,
            NotificationImageSlot.FrameBadgeUltraRare,
            NotificationImageSlot.FrameBadgeCompletion
        };

        public static string GetPath(NotificationStyleSettings style, NotificationImageSlot slot)
        {
            if (style == null)
            {
                return null;
            }

            switch (slot)
            {
                case NotificationImageSlot.Background:
                    return style.ToastBackgroundImagePath;
                case NotificationImageSlot.BadgeCommon:
                    return style.Toast.BadgeImages.CommonPath;
                case NotificationImageSlot.BadgeUncommon:
                    return style.Toast.BadgeImages.UncommonPath;
                case NotificationImageSlot.BadgeRare:
                    return style.Toast.BadgeImages.RarePath;
                case NotificationImageSlot.BadgeUltraRare:
                    return style.Toast.BadgeImages.UltraRarePath;
                case NotificationImageSlot.BadgeCompletion:
                    return style.Toast.BadgeImages.CompletionPath;
                case NotificationImageSlot.FrameBadgeCommon:
                    return style.Frame.BadgeImages.CommonPath;
                case NotificationImageSlot.FrameBadgeUncommon:
                    return style.Frame.BadgeImages.UncommonPath;
                case NotificationImageSlot.FrameBadgeRare:
                    return style.Frame.BadgeImages.RarePath;
                case NotificationImageSlot.FrameBadgeUltraRare:
                    return style.Frame.BadgeImages.UltraRarePath;
                case NotificationImageSlot.FrameBadgeCompletion:
                    return style.Frame.BadgeImages.CompletionPath;
                default:
                    return null;
            }
        }

        public static void SetPath(NotificationStyleSettings style, NotificationImageSlot slot, string path)
        {
            if (style == null)
            {
                return;
            }

            switch (slot)
            {
                case NotificationImageSlot.Background:
                    style.ToastBackgroundImagePath = path;
                    break;
                case NotificationImageSlot.BadgeCommon:
                    style.Toast.BadgeImages.CommonPath = path;
                    break;
                case NotificationImageSlot.BadgeUncommon:
                    style.Toast.BadgeImages.UncommonPath = path;
                    break;
                case NotificationImageSlot.BadgeRare:
                    style.Toast.BadgeImages.RarePath = path;
                    break;
                case NotificationImageSlot.BadgeUltraRare:
                    style.Toast.BadgeImages.UltraRarePath = path;
                    break;
                case NotificationImageSlot.BadgeCompletion:
                    style.Toast.BadgeImages.CompletionPath = path;
                    break;
                case NotificationImageSlot.FrameBadgeCommon:
                    style.Frame.BadgeImages.CommonPath = path;
                    break;
                case NotificationImageSlot.FrameBadgeUncommon:
                    style.Frame.BadgeImages.UncommonPath = path;
                    break;
                case NotificationImageSlot.FrameBadgeRare:
                    style.Frame.BadgeImages.RarePath = path;
                    break;
                case NotificationImageSlot.FrameBadgeUltraRare:
                    style.Frame.BadgeImages.UltraRarePath = path;
                    break;
                case NotificationImageSlot.FrameBadgeCompletion:
                    style.Frame.BadgeImages.CompletionPath = path;
                    break;
            }
        }
    }
}
