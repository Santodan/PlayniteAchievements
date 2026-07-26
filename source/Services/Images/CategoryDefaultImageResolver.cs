using PlayniteAchievements.Services.Achievements;
using System;

namespace PlayniteAchievements.Services.Images
{
    // Read side of provider-supplied default category art: probes the deterministic
    // cache path for (gameId, normalized category label) and returns it only when the file
    // exists. Defaults sit below user overrides and above the game icon/cover fallback.
    internal static class CategoryDefaultImageResolver
    {
        // Set by the plugin at startup. An accessor rather than a direct plugin reference keeps
        // this file compilable in the test project, which does not include the plugin entry point.
        internal static Func<DiskImageService> DiskImageServiceAccessor { get; set; }

        public static string Resolve(Guid? playniteGameId, string categoryLabel)
        {
            // Any downloaded provider default beats Playnite game metadata.
            if (!playniteGameId.HasValue || playniteGameId.Value == Guid.Empty)
            {
                return null;
            }

            var normalizedLabel = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(categoryLabel);
            if (string.Equals(
                normalizedLabel,
                AchievementCategoryTypeHelper.DefaultCategoryLabel,
                StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var diskImageService = DiskImageServiceAccessor?.Invoke();
            if (diskImageService == null)
            {
                return null;
            }

            try
            {
                return diskImageService.FindExistingDefaultCategoryImagePath(
                    playniteGameId.Value.ToString("D"),
                    normalizedLabel);
            }
            catch
            {
                return null;
            }
        }
    }
}
