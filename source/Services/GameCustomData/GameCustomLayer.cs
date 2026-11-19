using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Images;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace PlayniteAchievements.Services.GameCustomData
{
    /// <summary>One <c>CustomLayerAchievements</c> row, in the column types the database holds.</summary>
    internal sealed class CustomAchievementRow
    {
        public string PlayniteGameId { get; set; }
        public string ApiName { get; set; }
        public string DisplayName { get; set; }
        public string Description { get; set; }
        public long? Points { get; set; }
        public string TrophyType { get; set; }
        public string Category { get; set; }
        public string Tags { get; set; }
        public long? Hidden { get; set; }
        public long Cleared { get; set; }
        public long IsFiltered { get; set; }
        public long IsSummaryFiltered { get; set; }
        public string Note { get; set; }
        public long? GoalOrder { get; set; }
        public long? CustomOrder { get; set; }
        public string UnlockedIconPath { get; set; }
        public string LockedIconPath { get; set; }
        public string UnlockTimeUtc { get; set; }
        public long CapstoneAssigned { get; set; }
        public long IsAuthored { get; set; }
        public long Unlocked { get; set; }
        public long? ProgressNum { get; set; }
        public long? ProgressDenom { get; set; }
        public long? ScaledPoints { get; set; }
        public string CategoryKind { get; set; }
        public double? GlobalPercentUnlocked { get; set; }
        public string Rarity { get; set; }
        public long IsCapstone { get; set; }

        /// <summary>The written columns, in <see cref="Values"/> order.</summary>
        public const string Columns =
            "ApiName, DisplayName, Description, Points, TrophyType, Category, Tags, Hidden, Cleared, IsFiltered, IsSummaryFiltered, Note, GoalOrder, CustomOrder, UnlockedIconPath, LockedIconPath, UnlockTimeUtc, CapstoneAssigned, IsAuthored, Unlocked, ProgressNum, ProgressDenom, ScaledPoints, CategoryKind, GlobalPercentUnlocked, Rarity, IsCapstone";

        public object[] Values() => new object[]
        {
            ApiName, DisplayName, Description, Points, TrophyType, Category, Tags, Hidden, Cleared, IsFiltered, IsSummaryFiltered,
            Note, GoalOrder, CustomOrder, UnlockedIconPath, LockedIconPath, UnlockTimeUtc, CapstoneAssigned, IsAuthored, Unlocked,
            ProgressNum, ProgressDenom, ScaledPoints, CategoryKind, GlobalPercentUnlocked, Rarity, IsCapstone
        };

        public string Signature() =>
            string.Join("\n", Values().Select(value => Convert.ToString(value, CultureInfo.InvariantCulture)));

        /// <summary>A row read as PlayniteGameId then <see cref="Columns"/>.</summary>
        public static CustomAchievementRow From(object[] v) => new CustomAchievementRow
        {
            PlayniteGameId = Column.Text(v[0]), ApiName = Column.Text(v[1]), DisplayName = Column.Text(v[2]), Description = Column.Text(v[3]),
            Points = Column.Long(v[4]), TrophyType = Column.Text(v[5]), Category = Column.Text(v[6]), Tags = Column.Text(v[7]),
            Hidden = Column.Long(v[8]), Cleared = Column.Long(v[9]) ?? 0, IsFiltered = Column.Long(v[10]) ?? 0,
            IsSummaryFiltered = Column.Long(v[11]) ?? 0, Note = Column.Text(v[12]), GoalOrder = Column.Long(v[13]),
            CustomOrder = Column.Long(v[14]), UnlockedIconPath = Column.Text(v[15]), LockedIconPath = Column.Text(v[16]),
            UnlockTimeUtc = Column.Text(v[17]), CapstoneAssigned = Column.Long(v[18]) ?? 0, IsAuthored = Column.Long(v[19]) ?? 0,
            Unlocked = Column.Long(v[20]) ?? 0, ProgressNum = Column.Long(v[21]), ProgressDenom = Column.Long(v[22]),
            ScaledPoints = Column.Long(v[23]), CategoryKind = Column.Text(v[24]),
            GlobalPercentUnlocked = v[25] == null ? (double?)null : Convert.ToDouble(v[25], CultureInfo.InvariantCulture),
            Rarity = Column.Text(v[26]), IsCapstone = Column.Long(v[27]) ?? 0
        };

        /// <summary>True when the row carries one of the fields stored only here for a user override.</summary>
        public bool HasOverrideFields =>
            IsAuthored == 0 &&
            (DisplayName != null || Description != null || Points.HasValue || TrophyType != null || Category != null ||
             Tags != null || Hidden.HasValue || Cleared != 0 || Note != null || UnlockTimeUtc != null);

        public bool IsEmpty =>
            IsAuthored == 0 && !HasOverrideFields && UnlockedIconPath == null && LockedIconPath == null &&
            IsFiltered == 0 && IsSummaryFiltered == 0 && !GoalOrder.HasValue && !CustomOrder.HasValue && CapstoneAssigned == 0;
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

        /// <summary>True when <see cref="CustomGameStateRow.SettingsJson"/> holds the whole record.</summary>
        public bool StoredWhole { get; set; }
    }

    /// <summary>
    /// Maps a game's custom data to its rows in the custom layer tables and back. The tables are
    /// the only store of custom data.
    /// </summary>
    /// <remarks>
    /// Each field has one home. The columns a query reads hold the game flags, each category's
    /// kind and position, the custom and goal order, and every user override field but the icons;
    /// <c>CustomLayerGames.SettingsJson</c> holds the rest. Some columns hold what the views show
    /// rather than what the user stored (authored achievements with their overrides applied,
    /// resolved icons and art, filter and capstone flags), and are written from the record on
    /// every save but never read back. A record whose split does not read back exactly is stored
    /// whole in <c>SettingsJson</c> instead, so a save can never lose a value.
    /// </remarks>
    internal static class GameCustomLayer
    {
        private const string StoredWholeKey = "StoredWhole";

        internal static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.None,
            NullValueHandling = NullValueHandling.Ignore,
            DefaultValueHandling = DefaultValueHandling.Ignore
        };

        private static readonly JsonSerializer Serializer = JsonSerializer.Create(JsonSettings);

        /// <param name="data">A normalized record.</param>
        /// <param name="toStored">Maps an icon path to the form the database stores.</param>
        public static GameCustomLayerRows ToRows(GameCustomDataFile data, ManagedCustomIconService icons, Func<string, string> toStored)
        {
            var gameId = data.PlayniteGameId;
            var gameIdText = gameId.ToString("D");
            var rows = new GameCustomLayerRows
            {
                Achievements = BuildAchievementRows(data, icons, toStored),
                Categories = BuildCategoryRows(data, icons, toStored)
            };
            foreach (var row in rows.Achievements)
            {
                row.PlayniteGameId = gameIdText;
            }

            foreach (var row in rows.Categories)
            {
                row.PlayniteGameId = gameIdText;
            }

            rows.State = new CustomGameStateRow
            {
                PlayniteGameId = gameIdText,
                CapstonesMaterialized = data.CapstonesMaterialized ? 1 : 0,
                HasManualLink = data.ManualLink != null ? 1 : 0,
                ExcludedFromSummaries = data.ExcludedFromSummaries == true ? 1 : 0,
                ExcludedFromRefreshes = data.ExcludedFromRefreshes == true ? 1 : 0,
                UseSeparateLockedIcons = data.UseSeparateLockedIconsOverride == true ? 1 : 0,
                SettingsJson = Residual(data, rows.Achievements).ToString(Formatting.None)
            };

            if (!string.Equals(Canonical(FromRows(gameId, rows.State, rows.Achievements, rows.Categories)), Canonical(data), StringComparison.Ordinal))
            {
                var whole = JObject.FromObject(data, Serializer);
                whole[StoredWholeKey] = true;
                rows.State.SettingsJson = whole.ToString(Formatting.None);
                rows.StoredWhole = true;
            }

            return rows;
        }

        /// <summary>The record the rows hold, as it was saved (not normalized again).</summary>
        public static GameCustomDataFile FromRows(
            Guid gameId,
            CustomGameStateRow state,
            IEnumerable<CustomAchievementRow> achievements,
            IEnumerable<CustomCategoryRow> categories)
        {
            var settings = Parse(state?.SettingsJson);
            var whole = settings.Value<bool?>(StoredWholeKey) == true;
            settings.Remove(StoredWholeKey);
            var data = settings.ToObject<GameCustomDataFile>(Serializer);
            data.PlayniteGameId = gameId;
            if (whole)
            {
                return data;
            }

            data.CapstonesMaterialized = state.CapstonesMaterialized != 0;
            data.ExcludedFromSummaries = state.ExcludedFromSummaries != 0 ? true : (bool?)null;
            data.ExcludedFromRefreshes = state.ExcludedFromRefreshes != 0 ? true : (bool?)null;
            data.UseSeparateLockedIconsOverride = state.UseSeparateLockedIcons != 0 ? true : (bool?)null;

            var categoryRows = (categories ?? Enumerable.Empty<CustomCategoryRow>()).ToList();
            var kinds = new Dictionary<string, string>(CategoryPathHelper.LabelComparer);
            foreach (var row in categoryRows.Where(row => row.Kind != null))
            {
                kinds[row.Category] = row.Kind;
            }

            data.AchievementCategoryKinds = kinds.Count > 0 ? kinds : null;
            data.AchievementCategoryOrder = Ordered(categoryRows, row => row.SortIndex, row => row.Category);

            var achievementRows = (achievements ?? Enumerable.Empty<CustomAchievementRow>()).ToList();
            data.AchievementOrder = Ordered(achievementRows, row => row.CustomOrder, row => row.ApiName);
            data.GoalAchievementApiNames = Ordered(achievementRows, row => row.GoalOrder, row => row.ApiName);

            var overrides = data.AchievementOverrides ?? new Dictionary<string, AchievementOverride>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in achievementRows.Where(row => row.HasOverrideFields))
            {
                if (!overrides.TryGetValue(row.ApiName, out var entry) || entry == null)
                {
                    overrides[row.ApiName] = entry = new AchievementOverride();
                }

                entry.DisplayName = row.DisplayName;
                entry.Description = row.Description;
                entry.ClearDescription = IsCleared(row, AchievementField.Description);
                entry.Points = row.Points.HasValue ? (int)row.Points.Value : (int?)null;
                entry.TrophyType = row.TrophyType;
                entry.ClearTrophyType = IsCleared(row, AchievementField.TrophyType);
                entry.UnlockTimeUtc = Database.AchievementSqlFunctions.ParseUtc(row.UnlockTimeUtc);
                entry.ClearUnlockTime = IsCleared(row, AchievementField.UnlockTime);
                entry.Category = row.Category;
                entry.Tags = row.Tags;
                entry.ClearTags = IsCleared(row, AchievementField.Tags);
                entry.Note = row.Note;
                entry.Hidden = row.Hidden.HasValue ? row.Hidden.Value != 0 : (bool?)null;
            }

            data.AchievementOverrides = overrides.Count > 0 ? overrides : null;
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

        /// <summary>The record as JSON with every object's properties in name order, for comparing records.</summary>
        public static string Canonical(GameCustomDataFile data)
        {
            return data == null ? string.Empty : Sorted(JToken.FromObject(data, Serializer)).ToString(Formatting.None);
        }

        private static JToken Sorted(JToken token)
        {
            switch (token)
            {
                case JObject obj:
                    return new JObject(obj.Properties()
                        .OrderBy(property => property.Name, StringComparer.Ordinal)
                        .Select(property => new JProperty(property.Name, Sorted(property.Value))));
                case JArray array:
                    return new JArray(array.Select(Sorted));
                default:
                    return token;
            }
        }

        private static bool IsCleared(CustomAchievementRow row, AchievementField field) => (row.Cleared & (long)field) != 0;

        private static List<string> Ordered<T>(IEnumerable<T> rows, Func<T, long?> position, Func<T, string> name)
        {
            var ordered = rows.Where(row => position(row).HasValue).OrderBy(row => position(row).Value).Select(name).ToList();
            return ordered.Count > 0 ? ordered : null;
        }

        /// <summary>The record without the values its columns hold.</summary>
        private static JObject Residual(GameCustomDataFile data, IReadOnlyList<CustomAchievementRow> achievements)
        {
            var residual = data.Clone();
            residual.PlayniteGameId = Guid.Empty;
            residual.CapstonesMaterialized = false;
            residual.ExcludedFromSummaries = null;
            residual.ExcludedFromRefreshes = null;
            residual.UseSeparateLockedIconsOverride = null;
            residual.AchievementCategoryKinds = null;
            residual.AchievementCategoryOrder = null;
            residual.AchievementOrder = null;
            residual.GoalAchievementApiNames = null;
            if (residual.AchievementOverrides != null)
            {
                foreach (var row in achievements.Where(row => row.HasOverrideFields))
                {
                    if (residual.AchievementOverrides.TryGetValue(row.ApiName, out var entry) && entry != null)
                    {
                        residual.AchievementOverrides[row.ApiName] = new AchievementOverride
                        {
                            UnlockedIconPath = entry.UnlockedIconPath,
                            LockedIconPath = entry.LockedIconPath
                        };
                    }
                }

                residual.AchievementOverrides = GameCustomDataFile.CloneAchievementOverrideMap(residual.AchievementOverrides);
            }

            return JObject.FromObject(residual, Serializer);
        }

        /// <summary>
        /// A row per achievement the user customized: the override fields as stored, its flags and
        /// positions, and a row per authored achievement with the user's overrides applied, so the
        /// resolved views need nothing else.
        /// </summary>
        private static List<CustomAchievementRow> BuildAchievementRows(
            GameCustomDataFile data,
            ManagedCustomIconService icons,
            Func<string, string> toStored)
        {
            var rows = new Dictionary<string, CustomAchievementRow>(StringComparer.OrdinalIgnoreCase);
            var gameId = data.PlayniteGameId;
            var hasManualLink = data.ManualLink != null;
            var overrides = data.AchievementOverrides ?? new Dictionary<string, AchievementOverride>(StringComparer.OrdinalIgnoreCase);
            MarkPositions(rows, data.FilteredAchievementApiNames, (row, _) => row.IsFiltered = 1);
            MarkPositions(rows, data.SummaryFilteredAchievementApiNames, (row, _) => row.IsSummaryFiltered = 1);
            MarkPositions(rows, data.GoalAchievementApiNames, (row, index) => row.GoalOrder = index);
            MarkPositions(rows, data.AchievementOrder, (row, index) => row.CustomOrder = index);
            if (data.CapstonesMaterialized)
            {
                MarkPositions(rows, data.Capstones?.Select(capstone => capstone?.ApiName), (row, _) => row.CapstoneAssigned = 1);
            }

            foreach (var pair in overrides)
            {
                var apiName = NormalizeText(pair.Key);
                var entry = pair.Value;
                if (apiName == null || entry == null)
                {
                    continue;
                }

                var layer = entry.ToLayer();
                var unlockedIcon = ResolveIcon(entry.UnlockedIconPath, gameId, icons);
                var lockedIcon = ResolveIcon(entry.LockedIconPath, gameId, icons);
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
                row.UnlockedIconPath = toStored(unlockedIcon);
                row.LockedIconPath = toStored(lockedIcon);
                row.Note = note;
                row.UnlockTimeUtc = Iso(entry.UnlockTimeUtc);
            }

            // Authored achievements exist only in custom data; each row carries what the plugin
            // shows for it, with the user's overrides and capstone set already applied.
            foreach (var detail in CustomAchievementProjectionService.ProjectAchievements(gameId, data.CustomAchievements, icons))
            {
                overrides.TryGetValue(detail.ApiName, out var entry);
                detail.CustomLayer = entry?.ToLayer();
                AchievementLayers.Resolve(detail);
                entry?.ApplyUnlockTimeTo(detail, hasManualLink);
                detail.UnlockedIconPath = ResolveIcon(entry?.UnlockedIconPath, gameId, icons) ?? detail.UnlockedIconPath;
                detail.LockedIconPath = ResolveIcon(entry?.LockedIconPath, gameId, icons) ?? detail.LockedIconPath;
                detail.Category = CategoryPathHelper.NormalizePath(detail.Category);
                detail.CategoryKind = CategoryKindHelper.ResolveEffective(
                    CategoryEditor.ResolveStoredCategoryKind(data.AchievementCategoryKinds, detail.Category),
                    detail.CategoryKind);
                var row = Row(rows, detail.ApiName);
                row.IsAuthored = 1;
                row.DisplayName = detail.DisplayName;
                row.Description = detail.Description;
                row.UnlockedIconPath = toStored(detail.UnlockedIconPath);
                row.LockedIconPath = toStored(detail.LockedIconPath);
                row.Points = detail.Points;
                row.ScaledPoints = detail.ScaledPoints;
                row.TrophyType = detail.TrophyType;
                row.Category = detail.Category;
                row.CategoryKind = detail.CategoryKind;
                row.Tags = detail.Tags;
                row.Hidden = detail.Hidden ? 1 : 0;
                row.Unlocked = detail.Unlocked ? 1 : 0;
                row.UnlockTimeUtc = Iso(detail.UnlockTimeUtc);
                row.ProgressNum = detail.ProgressNum;
                row.ProgressDenom = detail.ProgressDenom;
                row.GlobalPercentUnlocked = detail.GlobalPercentUnlocked;
                row.Rarity = detail.Rarity.ToString();
                row.IsCapstone = (data.CapstonesMaterialized ? row.CapstoneAssigned != 0 : detail.IsCapstone) ? 1 : 0;
                row.Cleared = 0;
                row.Note = NormalizeText(entry?.Note);
            }

            return rows.Values.Where(row => !row.IsEmpty).Select(Blank).ToList();
        }

        /// <summary>The user's settings per category: its kind, its position in the custom order and its art.</summary>
        private static List<CustomCategoryRow> BuildCategoryRows(GameCustomDataFile data, ManagedCustomIconService icons, Func<string, string> toStored)
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
                var art = ResolveIcon(pair.Value?.Art, data.PlayniteGameId, icons);
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

        /// <summary>A stored icon override as the plugin displays it.</summary>
        private static string ResolveIcon(string value, Guid gameId, ManagedCustomIconService icons)
        {
            var normalized = NormalizeText(value);
            return normalized == null
                ? null
                : icons?.ResolveManagedDisplayPath(normalized, gameId.ToString("D")) ?? normalized;
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
