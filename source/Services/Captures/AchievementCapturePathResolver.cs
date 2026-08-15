using System;
using System.Linq;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services.Images;

namespace PlayniteAchievements.Services.Captures
{
    // Resolves an achievement's saved unlock-capture files into the four runtime-only path
    // properties (Clean / Notification / Framed / Video). Paths come from the cached per-game
    // scan, never from filename reconstruction: the NNN ordinal prefix is assigned at capture
    // time and the writer appends " (2)" on collisions, so concatenation cannot reproduce
    // what is actually on disk.
    internal static class AchievementCapturePathResolver
    {
        // Set by the plugin at startup. An accessor rather than a direct plugin reference keeps
        // this file compilable in the test project, which does not include the plugin entry point.
        internal static Func<CaptureLibraryService> CaptureLibraryAccessor { get; set; }

        /// <summary>Resolved capture paths for one achievement; a field is null when that variant is absent.</summary>
        public struct CapturePathStamp
        {
            public string Clean;
            public string Notification;
            public string Framed;
            public string Video;
        }

        /// <summary>
        /// Returns the cached capture set for a game, or null when the capture library is
        /// unavailable or the game's folder holds no captures. The folder-membership gate keeps
        /// library-wide passes cheap for games without captures. Never throws.
        /// </summary>
        public static GameCaptureSet ResolveGameSet(string gameName)
        {
            var service = CaptureLibraryAccessor?.Invoke();
            if (service == null || string.IsNullOrEmpty(gameName))
            {
                return null;
            }

            try
            {
                if (!service.GameFolderHasCaptures(gameName))
                {
                    return null;
                }

                var set = service.ScanGame(gameName);
                return set != null && set.HasAny ? set : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Stamps all four capture paths onto the achievement. A null set clears them, so a
        /// re-stamp after captures were deleted leaves no stale paths behind.
        /// </summary>
        public static void Apply(AchievementDetail achievement, GameCaptureSet set)
        {
            if (achievement == null)
            {
                return;
            }

            var stamp = ResolvePaths(set, achievement.DisplayName);
            achievement.CleanCapturePath = stamp.Clean;
            achievement.NotificationCapturePath = stamp.Notification;
            achievement.FramedCapturePath = stamp.Framed;
            achievement.VideoCapturePath = stamp.Video;
        }

        /// <summary>
        /// Resolves the four paths for one achievement display name against a scanned set. All
        /// fields are null when the set is null or the achievement has no captures.
        /// </summary>
        public static CapturePathStamp ResolvePaths(GameCaptureSet set, string achievementDisplayName)
        {
            var group = set?.FindGroup(AchievementIconCachePathBuilder.SanitizeSegment(achievementDisplayName));
            if (group == null)
            {
                return default(CapturePathStamp);
            }

            return new CapturePathStamp
            {
                Clean = SelectPath(group, CaptureVariant.Clean),
                Notification = SelectPath(group, CaptureVariant.Notification),
                Framed = SelectPath(group, CaptureVariant.Framed),
                Video = SelectPath(group, CaptureVariant.Video),
            };
        }

        /// <summary>
        /// One deterministic file per variant: the original capture wins over " (n)" collision
        /// duplicates. Ordering by path length first is what makes that hold — the plain name is
        /// the shortest, and a bare ordinal comparison would put " (2)" ahead of ".png" because
        /// space sorts before dot. With the original deleted, the lowest counter wins.
        /// </summary>
        internal static string SelectPath(AchievementCaptureGroup group, CaptureVariant variant)
        {
            return group
                .ForVariant(variant)
                .OrderBy(i => i.FilePath?.Length ?? 0)
                .ThenBy(i => i.FilePath, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault()?.FilePath;
        }
    }
}
