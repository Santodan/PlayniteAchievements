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

        private const string BrandingIconPackUri =
            "pack://application:,,,/PlayniteAchievements;component/Resources/BrandingIcon.png";

        /// <summary>
        /// The platinum that stands for finishing the game, or the default when it has none.
        /// </summary>
        /// <remarks>
        /// Several platinums means DLC trophy sets alongside the base game's, and only the base
        /// game's marks the game complete. Providers give the group a kind rather than leaving it to
        /// the label, so the base one is read off that kind instead of guessed from the category
        /// text. When nothing has a kind -- a game whose trophies were authored or came from a provider
        /// that does not group them -- the earliest in <paramref name="candidatesInOrder"/> wins.
        /// </remarks>
        public static T SelectPlatinum<T>(
            IEnumerable<T> candidatesInOrder,
            Func<T, string> trophyType,
            Func<T, string> categoryKind)
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
                CategoryKindHelper.IsBase(categoryKind(candidate)));
            return baseGame ?? platinums[0];
        }

        /// <summary>
        /// The capstone art a metadata source supplied for a game (a PlayStation platinum's icon),
        /// or null. Set at startup to read the metadata overlay's record.
        /// </summary>
        public static Func<Guid, string> MetadataArtAccessor { get; set; }

        /// <summary>
        /// The title and description that come with that art (the platinum's own), or nulls. Set at
        /// startup alongside <see cref="MetadataArtAccessor"/>.
        /// </summary>
        public static Func<Guid, (string Title, string Description)> MetadataTextAccessor { get; set; }

        /// <summary>
        /// The text a whole-game auto capstone takes: the borrowed platinum's when a metadata source
        /// supplied it, else the templates'.
        /// </summary>
        public static (string Title, string Description) ResolveWholeGameText(
            Guid gameId,
            AutoCapstoneTemplates templates,
            string gameName)
        {
            var borrowed = MetadataTextAccessor?.Invoke(gameId) ?? (null, null);
            return !string.IsNullOrWhiteSpace(borrowed.Title)
                ? (borrowed.Title, borrowed.Description ?? string.Empty)
                : AutoCapstoneText.Describe(templates, gameName, null);
        }

        /// <summary>
        /// The image to stand the capstone on: art from a metadata source when the user chose one,
        /// then the game's own icon, then its cover, then the plugin's mark, so it is never left
        /// without one.
        /// </summary>
        public static string ResolveIconSource(Game game, ILogger logger)
        {
            var metadataArt = game != null ? MetadataArtAccessor?.Invoke(game.Id) : null;
            if (!string.IsNullOrWhiteSpace(metadataArt))
            {
                return metadataArt;
            }

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
