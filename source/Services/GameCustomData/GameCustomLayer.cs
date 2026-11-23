using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Achievements;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace PlayniteAchievements.Services.GameCustomData
{
    /// <summary>
    /// One <c>CustomLayerAchievements</c> row, in the column types the database holds: the user's
    /// override of an achievement (<see cref="IsAuthored"/> 0), or an achievement the user authored
    /// as they defined it (<see cref="IsAuthored"/> 1). Each kind leaves the other's columns empty.
    /// </summary>
    internal sealed class CustomAchievementRow
    {
        public string PlayniteGameId { get; set; }
        public string ApiName { get; set; }
        public long IsAuthored { get; set; }
        public string DisplayName { get; set; }
        public string Description { get; set; }
        public long? Points { get; set; }
        public string TrophyType { get; set; }
        public string Category { get; set; }
        public string Tags { get; set; }
        public long? Hidden { get; set; }
        public string UnlockedIconPath { get; set; }
        public string LockedIconPath { get; set; }
        public string UnlockTimeUtc { get; set; }

        // Override rows only.
        public long Cleared { get; set; }
        public string Note { get; set; }
        public long? GoalOrder { get; set; }
        public long? CustomOrder { get; set; }
        public long? FilterOrder { get; set; }
        public long? SummaryFilterOrder { get; set; }
        public long? CapstoneOrder { get; set; }

        // Authored rows only.
        public long? Position { get; set; }
        public long Unlocked { get; set; }
        public long? ProgressNum { get; set; }
        public long? ProgressDenom { get; set; }
        public long? ScaledPoints { get; set; }
        public string CategoryKind { get; set; }
        public double? GlobalPercentUnlocked { get; set; }
        public string Rarity { get; set; }
        public long IsCapstone { get; set; }
        public long IsAutoCapstone { get; set; }
        public long IsWholeGameAutoCapstone { get; set; }

        /// <summary>The written columns, in <see cref="Values"/> order.</summary>
        public const string Columns =
            "ApiName, IsAuthored, DisplayName, Description, Points, TrophyType, Category, Tags, Hidden, UnlockedIconPath, LockedIconPath, UnlockTimeUtc, " +
            "Cleared, Note, GoalOrder, CustomOrder, FilterOrder, SummaryFilterOrder, CapstoneOrder, " +
            "Position, Unlocked, ProgressNum, ProgressDenom, ScaledPoints, CategoryKind, GlobalPercentUnlocked, Rarity, IsCapstone, IsAutoCapstone, IsWholeGameAutoCapstone";

        public object[] Values() => new object[]
        {
            ApiName, IsAuthored, DisplayName, Description, Points, TrophyType, Category, Tags, Hidden, UnlockedIconPath, LockedIconPath, UnlockTimeUtc,
            Cleared, Note, GoalOrder, CustomOrder, FilterOrder, SummaryFilterOrder, CapstoneOrder,
            Position, Unlocked, ProgressNum, ProgressDenom, ScaledPoints, CategoryKind, GlobalPercentUnlocked, Rarity, IsCapstone, IsAutoCapstone, IsWholeGameAutoCapstone
        };

        /// <summary>The row's identity within its game.</summary>
        public string Key => IsAuthored + ":" + ApiName;

        public string Signature() =>
            string.Join("\n", Values().Select(value => Convert.ToString(value, CultureInfo.InvariantCulture)));

        /// <summary>A row read as PlayniteGameId then <see cref="Columns"/>.</summary>
        public static CustomAchievementRow From(object[] v) => new CustomAchievementRow
        {
            PlayniteGameId = Column.Text(v[0]), ApiName = Column.Text(v[1]), IsAuthored = Column.Long(v[2]) ?? 0, DisplayName = Column.Text(v[3]),
            Description = Column.Text(v[4]), Points = Column.Long(v[5]), TrophyType = Column.Text(v[6]), Category = Column.Text(v[7]),
            Tags = Column.Text(v[8]), Hidden = Column.Long(v[9]), UnlockedIconPath = Column.Text(v[10]), LockedIconPath = Column.Text(v[11]),
            UnlockTimeUtc = Column.Text(v[12]), Cleared = Column.Long(v[13]) ?? 0, Note = Column.Text(v[14]), GoalOrder = Column.Long(v[15]),
            CustomOrder = Column.Long(v[16]), FilterOrder = Column.Long(v[17]), SummaryFilterOrder = Column.Long(v[18]), CapstoneOrder = Column.Long(v[19]),
            Position = Column.Long(v[20]), Unlocked = Column.Long(v[21]) ?? 0, ProgressNum = Column.Long(v[22]), ProgressDenom = Column.Long(v[23]),
            ScaledPoints = Column.Long(v[24]), CategoryKind = Column.Text(v[25]),
            GlobalPercentUnlocked = v[26] == null ? (double?)null : Convert.ToDouble(v[26], CultureInfo.InvariantCulture),
            Rarity = Column.Text(v[27]), IsCapstone = Column.Long(v[28]) ?? 0, IsAutoCapstone = Column.Long(v[29]) ?? 0,
            IsWholeGameAutoCapstone = Column.Long(v[30]) ?? 0
        };

        /// <summary>True for an override row that carries one of the override fields.</summary>
        public bool HasOverrideFields =>
            IsAuthored == 0 &&
            (DisplayName != null || Description != null || Points.HasValue || TrophyType != null || Category != null || Tags != null ||
             Hidden.HasValue || Cleared != 0 || Note != null || UnlockTimeUtc != null || UnlockedIconPath != null || LockedIconPath != null);

        public bool IsEmpty =>
            IsAuthored == 0 && !HasOverrideFields && !GoalOrder.HasValue && !CustomOrder.HasValue &&
            !FilterOrder.HasValue && !SummaryFilterOrder.HasValue && !CapstoneOrder.HasValue;
    }

    /// <summary>One <c>CustomLayerGames</c> row.</summary>
    internal sealed class CustomGameStateRow
    {
        public string PlayniteGameId { get; set; }
        public long CapstonesMaterialized { get; set; }
        public long HasManualLink { get; set; }
        public long ExcludedFromSummaries { get; set; }
        public long ExcludedFromRefreshes { get; set; }
        public long UseSeparateLockedIcons { get; set; }
        public string SettingsJson { get; set; }

        public const string Columns =
            "PlayniteGameId, CapstonesMaterialized, HasManualLink, ExcludedFromSummaries, ExcludedFromRefreshes, UseSeparateLockedIcons, SettingsJson";

        public string Signature() => string.Join(
            "\n", CapstonesMaterialized, HasManualLink, ExcludedFromSummaries, ExcludedFromRefreshes, UseSeparateLockedIcons, SettingsJson);

        /// <summary>A row read as <see cref="Columns"/>.</summary>
        public static CustomGameStateRow From(object[] v) => new CustomGameStateRow
        {
            PlayniteGameId = Column.Text(v[0]), CapstonesMaterialized = Column.Long(v[1]) ?? 0, HasManualLink = Column.Long(v[2]) ?? 0,
            ExcludedFromSummaries = Column.Long(v[3]) ?? 0, ExcludedFromRefreshes = Column.Long(v[4]) ?? 0,
            UseSeparateLockedIcons = Column.Long(v[5]) ?? 0, SettingsJson = Column.Text(v[6])
        };
    }

    /// <summary>One <c>CustomLayerCategories</c> row.</summary>
    internal sealed class CustomCategoryRow
    {
        public string PlayniteGameId { get; set; }
        public string Category { get; set; }
        public string Kind { get; set; }
        public long? SortIndex { get; set; }
        public string ImagePath { get; set; }

        public const string Columns = "PlayniteGameId, Category, Kind, SortIndex, ImagePath";

        public string Signature() => string.Join("\n", Category, Kind, SortIndex, ImagePath);

        /// <summary>A row read as <see cref="Columns"/>.</summary>
        public static CustomCategoryRow From(object[] v) => new CustomCategoryRow
        {
            PlayniteGameId = Column.Text(v[0]), Category = Column.Text(v[1]), Kind = Column.Text(v[2]),
            SortIndex = Column.Long(v[3]), ImagePath = Column.Text(v[4])
        };
    }

    // Rows are read with LoadObjects rather than Load<T>: SQLNado builds its type mapping once per
    // connection, which was about 18 ms of a 21 ms read of one game on a new connection.
    internal static class Column
    {
        public static string Text(object value) => value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);

        public static long? Long(object value) => value == null ? (long?)null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    /// <summary>A game's rows in the custom layer tables.</summary>
    internal sealed class GameCustomLayerRows
    {
        public CustomGameStateRow State { get; set; }

        public List<CustomAchievementRow> Achievements { get; set; } = new List<CustomAchievementRow>();

        public List<CustomCategoryRow> Categories { get; set; } = new List<CustomCategoryRow>();
    }

    /// <summary>
    /// Maps a game's custom data to its rows in the custom layer tables and back. The tables are
    /// the only store of custom data.
    /// </summary>
    /// <remarks>
    /// Each value has one home, and is read back from it. The columns hold the game flags; each
    /// category's kind, position and art; each achievement's override fields, icons and positions
    /// in the custom order, goals, filters and capstones; and each authored achievement as the
    /// user defined it, which the views resolve the user's override over as they do a platform
    /// achievement. <c>CustomLayerGames.SettingsJson</c> holds the rest. File paths are stored in
    /// the database's form and read back as absolute paths, so a saved record carries them in that
    /// form. Achievement rows are keyed by ApiName ignoring case, so an ApiName spelled two ways
    /// in one record reads back in its first spelling.
    /// </remarks>
    internal static class GameCustomLayer
    {
        internal static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.None,
            NullValueHandling = NullValueHandling.Ignore,
            DefaultValueHandling = DefaultValueHandling.Ignore
        };

        private static readonly JsonSerializer Serializer = JsonSerializer.Create(JsonSettings);

        /// <param name="data">A normalized record.</param>
        /// <param name="toStored">Maps a file path to the form the database stores.</param>
        public static GameCustomLayerRows ToRows(GameCustomDataFile data, Func<string, string> toStored)
        {
            var gameIdText = data.PlayniteGameId.ToString("D");
            var rows = new GameCustomLayerRows
            {
                Achievements = BuildAchievementRows(data, toStored),
                Categories = BuildCategoryRows(data, toStored),
                State = new CustomGameStateRow
                {
                    PlayniteGameId = gameIdText,
                    CapstonesMaterialized = data.CapstonesMaterialized ? 1 : 0,
                    HasManualLink = data.ManualLink != null ? 1 : 0,
                    ExcludedFromSummaries = data.ExcludedFromSummaries == true ? 1 : 0,
                    ExcludedFromRefreshes = data.ExcludedFromRefreshes == true ? 1 : 0,
                    UseSeparateLockedIcons = data.UseSeparateLockedIconsOverride == true ? 1 : 0,
                    SettingsJson = Residual(data).ToString(Formatting.None)
                }
            };
            foreach (var row in rows.Achievements)
            {
                row.PlayniteGameId = gameIdText;
            }

            foreach (var row in rows.Categories)
            {
                row.PlayniteGameId = gameIdText;
            }

            return rows;
        }

        /// <summary>The record the rows hold, as it was saved (not normalized again).</summary>
        /// <param name="fromStored">Maps a stored file path to an absolute one.</param>
        public static GameCustomDataFile FromRows(
            Guid gameId,
            CustomGameStateRow state,
            IEnumerable<CustomAchievementRow> achievements,
            IEnumerable<CustomCategoryRow> categories,
            Func<string, string> fromStored)
        {
            var data = Parse(state?.SettingsJson).ToObject<GameCustomDataFile>(Serializer);
            data.PlayniteGameId = gameId;
            data.CapstonesMaterialized = state.CapstonesMaterialized != 0;
            data.ExcludedFromSummaries = state.ExcludedFromSummaries != 0 ? true : (bool?)null;
            data.ExcludedFromRefreshes = state.ExcludedFromRefreshes != 0 ? true : (bool?)null;
            data.UseSeparateLockedIconsOverride = state.UseSeparateLockedIcons != 0 ? true : (bool?)null;

            // In name order, so a record reads back alike however its rows were listed.
            var categoryRows = (categories ?? Enumerable.Empty<CustomCategoryRow>()).OrderBy(row => row.Category, StringComparer.Ordinal).ToList();
            var kinds = new Dictionary<string, string>(CategoryPathHelper.LabelComparer);
            var art = new Dictionary<string, CategoryImageOverrideData>(CategoryPathHelper.LabelComparer);
            foreach (var row in categoryRows)
            {
                if (row.Kind != null)
                {
                    kinds[row.Category] = row.Kind;
                }

                if (row.ImagePath != null)
                {
                    art[row.Category] = new CategoryImageOverrideData { Art = fromStored(row.ImagePath) };
                }
            }

            data.AchievementCategoryKinds = kinds.Count > 0 ? kinds : null;
            data.AchievementCategoryImageOverrides = art.Count > 0 ? art : null;
            data.AchievementCategoryOrder = Ordered(categoryRows, row => row.SortIndex, row => row.Category);

            var achievementRows = (achievements ?? Enumerable.Empty<CustomAchievementRow>()).OrderBy(row => row.ApiName, StringComparer.Ordinal).ToList();
            var overrideRows = achievementRows.Where(row => row.IsAuthored == 0).ToList();
            data.AchievementOrder = Ordered(overrideRows, row => row.CustomOrder, row => row.ApiName);
            data.GoalAchievementApiNames = Ordered(overrideRows, row => row.GoalOrder, row => row.ApiName);
            data.FilteredAchievementApiNames = Ordered(overrideRows, row => row.FilterOrder, row => row.ApiName);
            data.SummaryFilteredAchievementApiNames = Ordered(overrideRows, row => row.SummaryFilterOrder, row => row.ApiName);
            data.Capstones = Ordered(overrideRows, row => row.CapstoneOrder, row => row.ApiName)?
                .Select(apiName => new CapstoneAssignment { ApiName = apiName })
                .ToList();

            var overrides = new Dictionary<string, AchievementOverride>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in overrideRows.Where(row => row.HasOverrideFields))
            {
                overrides[row.ApiName] = new AchievementOverride
                {
                    DisplayName = row.DisplayName, Description = row.Description, ClearDescription = IsCleared(row, AchievementField.Description),
                    Points = (int?)row.Points, TrophyType = row.TrophyType, ClearTrophyType = IsCleared(row, AchievementField.TrophyType),
                    UnlockTimeUtc = Database.AchievementSqlFunctions.ParseUtc(row.UnlockTimeUtc), ClearUnlockTime = IsCleared(row, AchievementField.UnlockTime),
                    Category = row.Category, Tags = row.Tags, ClearTags = IsCleared(row, AchievementField.Tags), Note = row.Note,
                    UnlockedIconPath = fromStored(row.UnlockedIconPath), LockedIconPath = fromStored(row.LockedIconPath),
                    Hidden = row.Hidden.HasValue ? row.Hidden.Value != 0 : (bool?)null
                };
            }

            data.AchievementOverrides = overrides.Count > 0 ? overrides : null;

            var authored = achievementRows
                .Where(row => row.IsAuthored != 0)
                .OrderBy(row => row.Position)
                .Select(row => new CustomAchievementDefinition
                {
                    Id = row.ApiName.Substring(CustomAchievementProjectionService.ApiNamePrefix.Length), DisplayName = row.DisplayName,
                    Description = row.Description, Unlocked = row.Unlocked != 0, UnlockTimeUtc = Database.AchievementSqlFunctions.ParseUtc(row.UnlockTimeUtc),
                    UnlockedIconPath = fromStored(row.UnlockedIconPath), LockedIconPath = fromStored(row.LockedIconPath), Points = (int?)row.Points,
                    ScaledPoints = (int?)row.ScaledPoints, Category = row.Category, Tags = row.Tags, CategoryKind = row.CategoryKind,
                    TrophyType = row.TrophyType, Hidden = row.Hidden == 1, IsCapstone = row.IsCapstone != 0, Rarity = row.Rarity,
                    GlobalPercentUnlocked = row.GlobalPercentUnlocked, ProgressNum = (int?)row.ProgressNum, ProgressDenom = (int?)row.ProgressDenom,
                    IsAutoCapstone = row.IsAutoCapstone != 0, IsWholeGameAutoCapstone = row.IsWholeGameAutoCapstone != 0
                })
                .ToList();
            data.CustomAchievements = authored.Count > 0 ? authored : null;
            return data;
        }

        /// <summary>The stored schema version of a SettingsJson value, 0 when it has none.</summary>
        public static int SchemaVersionOf(string settingsJson)
        {
            try
            {
                return Parse(settingsJson).Value<int?>(nameof(GameCustomDataFile.SchemaVersion)) ?? 0;
            }
            catch (JsonException)
            {
                return 0;
            }
        }

        // Text that looks like a date stays text; the serializer reads dates where the record has them.
        private static JObject Parse(string json)
        {
            using (var reader = new JsonTextReader(new StringReader(json ?? "{}")) { DateParseHandling = DateParseHandling.None })
            {
                return JObject.Load(reader);
            }
        }

        private static bool IsCleared(CustomAchievementRow row, AchievementField field) => (row.Cleared & (long)field) != 0;

        private static List<string> Ordered<T>(IEnumerable<T> rows, Func<T, long?> position, Func<T, string> name)
        {
            var ordered = rows.Where(row => position(row).HasValue).OrderBy(row => position(row).Value).Select(name).ToList();
            return ordered.Count > 0 ? ordered : null;
        }

        /// <summary>The record without the values its columns hold.</summary>
        private static JObject Residual(GameCustomDataFile data)
        {
            var residual = data.Clone();
            residual.PlayniteGameId = Guid.Empty;
            residual.CapstonesMaterialized = false;
            residual.ExcludedFromSummaries = null;
            residual.ExcludedFromRefreshes = null;
            residual.UseSeparateLockedIconsOverride = null;
            residual.AchievementCategoryKinds = null;
            residual.AchievementCategoryOrder = null;
            residual.AchievementCategoryImageOverrides = null;
            residual.AchievementOrder = null;
            residual.GoalAchievementApiNames = null;
            residual.FilteredAchievementApiNames = null;
            residual.SummaryFilteredAchievementApiNames = null;
            residual.Capstones = null;
            residual.AchievementOverrides = null;
            residual.CustomAchievements = null;
            return JObject.FromObject(residual, Serializer);
        }

        /// <summary>
        /// A row per achievement the user customized, with the override fields as stored and its
        /// positions, and a row per authored achievement as the user defined it.
        /// </summary>
        private static List<CustomAchievementRow> BuildAchievementRows(GameCustomDataFile data, Func<string, string> toStored)
        {
            var rows = new Dictionary<string, CustomAchievementRow>(StringComparer.OrdinalIgnoreCase);
            MarkPositions(rows, data.FilteredAchievementApiNames, (row, index) => row.FilterOrder = index);
            MarkPositions(rows, data.SummaryFilteredAchievementApiNames, (row, index) => row.SummaryFilterOrder = index);
            MarkPositions(rows, data.GoalAchievementApiNames, (row, index) => row.GoalOrder = index);
            MarkPositions(rows, data.AchievementOrder, (row, index) => row.CustomOrder = index);
            MarkPositions(rows, data.Capstones?.Select(capstone => capstone?.ApiName), (row, index) => row.CapstoneOrder = index);

            foreach (var pair in data.AchievementOverrides ?? new Dictionary<string, AchievementOverride>())
            {
                var apiName = NormalizeText(pair.Key);
                var entry = pair.Value;
                if (apiName == null || entry == null)
                {
                    continue;
                }

                var layer = entry.ToLayer();
                var unlockedIcon = toStored(NormalizeText(entry.UnlockedIconPath));
                var lockedIcon = toStored(NormalizeText(entry.LockedIconPath));
                var note = NormalizeText(entry.Note);
                // Stored as the user set them; the views skip them for a manually tracked game.
                var clearUnlockTime = !entry.UnlockTimeUtc.HasValue && entry.ClearUnlockTime;
                if (layer.IsEmpty && unlockedIcon == null && lockedIcon == null && note == null && !entry.UnlockTimeUtc.HasValue && !clearUnlockTime)
                {
                    continue;
                }

                var row = Row(rows, apiName);
                row.DisplayName = layer.DisplayName;
                row.Description = layer.Description;
                row.Points = layer.Points;
                row.TrophyType = layer.TrophyType;
                row.Category = layer.Category;
                // Derived tags are the platform's; the views add them back, as the resolver does.
                row.Tags = AchievementTagHelper.StripDerived(layer.Tags);
                row.Hidden = layer.Hidden.HasValue ? (layer.Hidden.Value ? 1 : 0) : (long?)null;
                row.Cleared |= (long)layer.Cleared | (clearUnlockTime ? (long)AchievementField.UnlockTime : 0);
                row.UnlockedIconPath = unlockedIcon;
                row.LockedIconPath = lockedIcon;
                row.Note = note;
                row.UnlockTimeUtc = Iso(entry.UnlockTimeUtc);
            }

            var authored = (data.CustomAchievements ?? new List<CustomAchievementDefinition>()).Select((definition, index) => new CustomAchievementRow
            {
                ApiName = CustomAchievementProjectionService.BuildApiName(definition.Id), IsAuthored = 1, Position = index,
                DisplayName = definition.DisplayName, Description = definition.Description, Unlocked = definition.Unlocked ? 1 : 0,
                UnlockTimeUtc = Iso(definition.UnlockTimeUtc), UnlockedIconPath = toStored(NormalizeText(definition.UnlockedIconPath)),
                LockedIconPath = toStored(NormalizeText(definition.LockedIconPath)), Points = definition.Points, ScaledPoints = definition.ScaledPoints,
                Category = definition.Category, Tags = definition.Tags, CategoryKind = definition.CategoryKind, TrophyType = definition.TrophyType,
                Hidden = definition.Hidden ? 1 : 0, IsCapstone = definition.IsCapstone ? 1 : 0, Rarity = definition.Rarity,
                GlobalPercentUnlocked = definition.GlobalPercentUnlocked, ProgressNum = definition.ProgressNum, ProgressDenom = definition.ProgressDenom,
                IsAutoCapstone = definition.IsAutoCapstone ? 1 : 0, IsWholeGameAutoCapstone = definition.IsWholeGameAutoCapstone ? 1 : 0
            });

            return rows.Values.Where(row => !row.IsEmpty).Concat(authored).Select(Blank).ToList();
        }

        /// <summary>The user's settings per category: its kind, its position in the custom order and its art.</summary>
        private static List<CustomCategoryRow> BuildCategoryRows(GameCustomDataFile data, Func<string, string> toStored)
        {
            var rows = new Dictionary<string, CustomCategoryRow>(StringComparer.OrdinalIgnoreCase);
            CustomCategoryRow Category(string name)
            {
                if (!rows.TryGetValue(name, out var row))
                {
                    rows[name] = row = new CustomCategoryRow { Category = name };
                }

                return row;
            }

            foreach (var pair in data.AchievementCategoryKinds ?? new Dictionary<string, string>())
            {
                var category = CategoryPathHelper.NormalizePath(pair.Key);
                var kind = CategoryKindHelper.NormalizeStored(pair.Value);
                if (category != null && kind != null)
                {
                    Category(category).Kind = kind;
                }
            }

            var order = data.AchievementCategoryOrder ?? new List<string>();
            for (var i = 0; i < order.Count; i++)
            {
                var category = CategoryPathHelper.NormalizePath(order[i]);
                if (category != null && !Category(category).SortIndex.HasValue)
                {
                    Category(category).SortIndex = i;
                }
            }

            foreach (var pair in data.AchievementCategoryImageOverrides ?? new Dictionary<string, CategoryImageOverrideData>())
            {
                var category = CategoryPathHelper.NormalizePath(pair.Key);
                var art = NormalizeText(pair.Value?.Art);
                if (category != null && art != null)
                {
                    Category(category).ImagePath = toStored(art);
                }
            }

            return rows.Values.Select(row =>
            {
                row.Kind = NormalizeText(row.Kind);
                row.ImagePath = NormalizeText(row.ImagePath);
                return row;
            }).ToList();
        }

        // Blank text is stored as NULL, so the row in memory is the row the database returns.
        private static CustomAchievementRow Blank(CustomAchievementRow row)
        {
            row.DisplayName = NormalizeText(row.DisplayName);
            row.Description = NormalizeText(row.Description);
            row.TrophyType = NormalizeText(row.TrophyType);
            row.Category = NormalizeText(row.Category);
            row.Tags = NormalizeText(row.Tags);
            row.Note = NormalizeText(row.Note);
            row.UnlockedIconPath = NormalizeText(row.UnlockedIconPath);
            row.LockedIconPath = NormalizeText(row.LockedIconPath);
            row.CategoryKind = NormalizeText(row.CategoryKind);
            row.Rarity = NormalizeText(row.Rarity);
            row.ApiName = row.ApiName.Trim();
            return row;
        }

        private static void MarkPositions(
            Dictionary<string, CustomAchievementRow> rows,
            IEnumerable<string> apiNames,
            Action<CustomAchievementRow, int> mark)
        {
            var index = 0;
            foreach (var apiName in apiNames ?? Enumerable.Empty<string>())
            {
                var normalized = NormalizeText(apiName);
                if (normalized != null)
                {
                    mark(Row(rows, normalized), index);
                }

                index++;
            }
        }

        private static CustomAchievementRow Row(Dictionary<string, CustomAchievementRow> rows, string apiName)
        {
            if (!rows.TryGetValue(apiName, out var row) || row == null)
            {
                rows[apiName] = row = new CustomAchievementRow { ApiName = apiName };
            }

            return row;
        }

        private static string Iso(DateTime? value) =>
            value.HasValue ? Common.DateTimeUtilities.AsUtcKind(value.Value).ToString("O", CultureInfo.InvariantCulture) : null;

        private static string NormalizeText(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            return normalized.Length == 0 ? null : normalized;
        }
    }
}
