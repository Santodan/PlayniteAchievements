using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PlayniteAchievements.Services.Achievements
{
    /// <summary>
    /// What an auto capstone is made of, shared by the editor's Auto Capstone button and automatic
    /// generation so the two author the same thing.
    /// </summary>
    public static class AutoCapstoneTemplate
    {
        public const string PlatinumTrophyType = "platinum";

        public const string DescriptionKey = "LOCPlayAch_ManageAchievements_Custom_AutoCapstoneDescription";

        private const string BaseCategoryType = "Base";

        private const string BrandingIconPackUri =
            "pack://application:,,,/PlayniteAchievements;component/Resources/BrandingIcon.png";

        /// <summary>
        /// The platinum that stands for finishing the game, or the default when it has none.
        /// </summary>
        /// <remarks>
        /// Several platinums means DLC trophy sets alongside the base game's, and only the base
        /// game's marks the game complete. Providers type the group rather than leaving it to the
        /// label, so the base one is read off that type instead of guessed from the category text.
        /// When nothing is typed -- a game whose trophies were authored or came from a provider
        /// that does not group them -- the earliest in <paramref name="candidatesInOrder"/> wins.
        /// </remarks>
        public static T SelectPlatinum<T>(
            IEnumerable<T> candidatesInOrder,
            Func<T, string> trophyType,
            Func<T, string> categoryType)
            where T : class
        {
            var platinums = (candidatesInOrder ?? Enumerable.Empty<T>())
                .Where(candidate => candidate != null &&
                                    string.Equals(
                                        (trophyType(candidate) ?? string.Empty).Trim(),
                                        PlatinumTrophyType,
                                        StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (platinums.Count <= 1)
            {
                return platinums.FirstOrDefault();
            }

            var baseGame = platinums.FirstOrDefault(candidate =>
                AchievementCategoryTypeHelper.ParseValues(categoryType(candidate))
                    .Any(value => string.Equals(value, BaseCategoryType, StringComparison.OrdinalIgnoreCase)));
            return baseGame ?? platinums[0];
        }

        /// <summary>
        /// The image to stand the capstone on: the game's own icon, then its cover, then the
        /// plugin's mark, so it is never left without one.
        /// </summary>
        public static string ResolveIconSource(Game game, ILogger logger)
        {
            var icon = ResolvePlayniteAssetFile(game?.Icon);
            if (!string.IsNullOrWhiteSpace(icon))
            {
                return icon;
            }

            var cover = ResolvePlayniteAssetFile(game?.CoverImage);
            return !string.IsNullOrWhiteSpace(cover) ? cover : ResolveBrandingIconFile(logger);
        }

        private static string ResolvePlayniteAssetFile(string databasePath)
        {
            var normalized = (databasePath ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return null;
            }

            var full = API.Instance?.Database?.GetFullFilePath(normalized);
            return !string.IsNullOrWhiteSpace(full) && File.Exists(full) ? full : null;
        }

        /// <summary>
        /// Unpacks the plugin's mark to a file, because an achievement's icon is stored as a path
        /// and the mark ships inside the assembly.
        /// </summary>
        private static string ResolveBrandingIconFile(ILogger logger)
        {
            try
            {
                var target = Path.Combine(Path.GetTempPath(), "playniteachievements-capstone.png");
                if (File.Exists(target))
                {
                    return target;
                }

                var resource = System.Windows.Application.GetResourceStream(new Uri(BrandingIconPackUri));
                if (resource?.Stream == null)
                {
                    return null;
                }

                using (var source = resource.Stream)
                using (var file = File.Create(target))
                {
                    source.CopyTo(file);
                }

                return target;
            }
            catch (Exception ex)
            {
                logger?.Warn(ex, "Failed unpacking the branding icon for the automatic capstone.");
                return null;
            }
        }
    }
}
