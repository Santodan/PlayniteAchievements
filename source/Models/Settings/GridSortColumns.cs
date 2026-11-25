using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Models.Settings
{
    /// <summary>Which grid surfaces offer a sort column as their default sort.</summary>
    public enum GridSortColumnScope
    {
        /// <summary>Every surface of the grid kind.</summary>
        Always,

        /// <summary>Achievement grids that list more than one game (the Game column).</summary>
        MultiGame,

        /// <summary>Game grids over the user's own library (library dates, playtime, locked count).</summary>
        Library,

        /// <summary>Game grids over a friend's games (the Owned column).</summary>
        FriendGames,

        /// <summary>Achievement grids over friends' unlocks (the Friend column).</summary>
        Friend,

        /// <summary>
        /// Game grids with per-game achievement stats; the grids over every friend's games at
        /// once leave these columns out.
        /// </summary>
        PerGameStats
    }

    /// <summary>A column a grid can take as its default sort: its sort member path and header label.</summary>
    public sealed class GridSortColumn
    {
        public GridSortColumn(string key, string labelKey, GridSortColumnScope scope = GridSortColumnScope.Always)
        {
            Key = key;
            LabelKey = labelKey;
            Scope = scope;
        }

        /// <summary>The column's sort member path, which the grid options store.</summary>
        public string Key { get; }

        public string LabelKey { get; }

        public GridSortColumnScope Scope { get; }
    }

    /// <summary>
    /// The columns achievement and game grids can take as their default sort, keyed by sort
    /// member path like the control bar's Sort By. Columns that share a path (Achievement and
    /// Achievement Name, the three rarity columns) are listed once, under the first column's
    /// label. The empty key keeps the grid's source order.
    /// </summary>
    public static class GridSortColumns
    {
        /// <summary>The key that keeps the grid's source order rather than sorting.</summary>
        public const string SourceOrder = "";

        /// <summary>
        /// The JSON name of the enum sort that grid records held before column keys. Older
        /// settings, migrations and Workshop packages still carry it; the records read it once.
        /// </summary>
        public const string LegacySortModeName = "SortMode";

        public static readonly IReadOnlyList<GridSortColumn> Achievement = new[]
        {
            new GridSortColumn("DisplayName", "LOCPlayAch_Column_Achievement"),
            new GridSortColumn("HasAchievementNote", "LOCPlayAch_ManageAchievements_Notes_Note"),
            new GridSortColumn("SortingName", "LOCPlayAch_Column_Game", GridSortColumnScope.MultiGame),
            new GridSortColumn("FriendName", "LOCPlayAch_Column_Friend", GridSortColumnScope.Friend),
            new GridSortColumn("UnlockTime", "LOCPlayAch_Common_Unlocked"),
            new GridSortColumn("CategoryKind", "LOCPlayAch_Common_Type"),
            new GridSortColumn("CategoryType", "LOCPlayAch_Common_Tags"),
            new GridSortColumn("CategoryLabel", "LOCPlayAch_Common_Category"),
            new GridSortColumn("TrophyType", "LOCPlayAch_Column_Trophy"),
            new GridSortColumn("RaritySortValue", "LOCPlayAch_Column_Rarity"),
            new GridSortColumn("CollectionScore", "LOCPlayAch_Score_Collection"),
            new GridSortColumn("PrestigeScore", "LOCPlayAch_Score_Prestige"),
            new GridSortColumn("Points", "LOCPlayAch_Column_Points")
        };

        public static readonly IReadOnlyList<GridSortColumn> GameSummaries = new[]
        {
            new GridSortColumn("SortingName", "LOCPlayAch_Column_Game"),
            new GridSortColumn("Owned", "LOCPlayAch_Column_Owned", GridSortColumnScope.FriendGames),
            new GridSortColumn("FriendCount", "LOCPlayAch_Settings_Friends", GridSortColumnScope.FriendGames),
            new GridSortColumn("UniqueFriendUnlockedAchievementsCount", "LOCPlayAch_Column_Unlocks", GridSortColumnScope.FriendGames),
            new GridSortColumn("LastPlayed", "LOCPlayAch_Common_LastPlayed"),
            new GridSortColumn("LastUnlockUtc", "LOCPlayAch_Common_LastUnlock"),
            new GridSortColumn("PlaytimeSeconds", "LOCPlayAch_Common_Label_Playtime", GridSortColumnScope.Library),
            new GridSortColumn("Added", "LOCPlayAch_Common_DateAdded", GridSortColumnScope.Library),
            new GridSortColumn("ReleaseDate", "LOCPlayAch_Common_ReleaseDate", GridSortColumnScope.Library),
            new GridSortColumn("Progression", "LOCPlayAch_Progress", GridSortColumnScope.PerGameStats),
            new GridSortColumn("TotalAchievements", "LOCPlayAch_Column_Total", GridSortColumnScope.PerGameStats),
            new GridSortColumn("RemainingAchievements", "LOCPlayAch_Common_Locked", GridSortColumnScope.Library),
            new GridSortColumn("CollectionScore", "LOCPlayAch_Score_Collection", GridSortColumnScope.PerGameStats),
            new GridSortColumn("PrestigeScore", "LOCPlayAch_Score_Prestige", GridSortColumnScope.PerGameStats),
            new GridSortColumn("Points", "LOCPlayAch_Column_Points", GridSortColumnScope.PerGameStats)
        };

        public static bool IsSourceOrder(string key) => string.IsNullOrEmpty(key);

        /// <summary>
        /// The columns of <paramref name="columns"/> a surface offers: every unscoped column, and
        /// each scoped one whose scope <paramref name="hasScope"/> says the surface has.
        /// </summary>
        public static IEnumerable<GridSortColumn> Offered(
            IEnumerable<GridSortColumn> columns,
            Func<GridSortColumnScope, bool> hasScope)
        {
            foreach (var column in columns ?? Array.Empty<GridSortColumn>())
            {
                if (column.Scope == GridSortColumnScope.Always || hasScope?.Invoke(column.Scope) == true)
                {
                    yield return column;
                }
            }
        }

        /// <summary>The catalog's label key for a sort member path, or null when it lists no such sort.</summary>
        public static string LabelKeyFor(IEnumerable<GridSortColumn> columns, string sortMemberPath)
        {
            foreach (var column in columns ?? Array.Empty<GridSortColumn>())
            {
                if (string.Equals(column.Key, sortMemberPath, StringComparison.Ordinal))
                {
                    return column.LabelKey;
                }
            }

            return null;
        }

        /// <summary>The column key for an achievement grid sort saved before column keys.</summary>
        public static string FromAchievementMode(CompactListSortMode mode)
        {
            switch (mode)
            {
                case CompactListSortMode.UnlockTime:
                    return "UnlockTime";
                case CompactListSortMode.Rarity:
                    return "RaritySortValue";
                default:
                    return SourceOrder;
            }
        }

        /// <summary>The column key for a game grid sort saved before column keys.</summary>
        public static string FromGameSummariesMode(GameSummariesSortMode mode)
        {
            switch (mode)
            {
                case GameSummariesSortMode.LastPlayed:
                    return "LastPlayed";
                case GameSummariesSortMode.TotalAchievements:
                    return "TotalAchievements";
                case GameSummariesSortMode.Progress:
                    return "Progression";
                case GameSummariesSortMode.Alphabetical:
                    return "SortingName";
                case GameSummariesSortMode.PinOrder:
                    return SourceOrder;
                case GameSummariesSortMode.DateAdded:
                    return "Added";
                case GameSummariesSortMode.ReleaseDate:
                    return "ReleaseDate";
                case GameSummariesSortMode.Playtime:
                    return "PlaytimeSeconds";
                case GameSummariesSortMode.Remaining:
                    return "RemainingAchievements";
                default:
                    return "LastUnlockUtc";
            }
        }

        /// <summary>Whether two column keys name the same sort, treating null and empty alike.</summary>
        public static bool AreSame(string a, string b) =>
            string.Equals(a ?? SourceOrder, b ?? SourceOrder, StringComparison.Ordinal);
    }
}
