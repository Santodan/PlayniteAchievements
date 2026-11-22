using Newtonsoft.Json;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers.Manual;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PlayniteAchievements.Services.LinkedFiles
{
    /// <summary>One row's cells read as achievement fields. Only mapped, non-empty cells set a field.</summary>
    public sealed class LinkedFileRowValues
    {
        /// <summary>True when the row says anything about unlock state.</summary>
        public bool HasUnlockInfo { get; set; }

        public bool Unlocked { get; set; }

        /// <summary>The unlock time the row gives, when it gives one and the row is unlocked.</summary>
        public DateTime? UnlockTimeUtc { get; set; }

        public int? ProgressNum { get; set; }

        public int? ProgressDenom { get; set; }

        public string DisplayName { get; set; }

        public string Description { get; set; }

        public bool? Hidden { get; set; }

        public string UnlockedIconPath { get; set; }

        public string LockedIconPath { get; set; }

        public int? Points { get; set; }

        /// <summary>Lowercase bronze, silver, gold or platinum.</summary>
        public string TrophyType { get; set; }

        /// <summary>True when the row gives a rarity, as a percent or a tier.</summary>
        public bool HasRarity { get; set; }

        public double? RarityPercent { get; set; }

        public string RarityTier { get; set; }

        /// <summary>The stored category path.</summary>
        public string Category { get; set; }

        public List<string> Errors { get; } = new List<string>();

        /// <summary>The fields whose cells could not be read.</summary>
        public HashSet<LinkedFileField> FailedFields { get; } = new HashSet<LinkedFileField>();

        public bool HasDefinitionFields =>
            DisplayName != null || Description != null || Hidden.HasValue ||
            UnlockedIconPath != null || LockedIconPath != null || Points.HasValue ||
            TrophyType != null || HasRarity || Category != null;

        public static LinkedFileRowValues Parse(LinkedFileTable table, int rowIndex, LinkedFileSchema schema, string fileDirectory, DateTime nowUtc)
        {
            var values = new LinkedFileRowValues();
            string Cell(LinkedFileField field, out bool mapped)
            {
                mapped = false;
                if (!schema.TryGetColumn(field, out var column))
                {
                    return null;
                }

                var index = table.IndexOfColumn(column);
                if (index < 0)
                {
                    return null;
                }

                mapped = true;
                var text = table.Cell(rowIndex, index).Trim();
                return text.Length == 0 ? null : text;
            }

            var unlockedCell = Cell(LinkedFileField.Unlocked, out _);
            var timeCell = Cell(LinkedFileField.UnlockTime, out var timeMapped);

            DateTime? parsedTime = null;
            var timeValid = true;
            if (timeCell != null)
            {
                timeValid = LinkedFileValueParser.TryParseUnlockTimeUtc(timeCell, schema, out parsedTime);
                if (!timeValid)
                {
                    values.FailedFields.Add(LinkedFileField.UnlockTime);
                    values.Errors.Add("Unlock time '" + timeCell + "' is not a date and time.");
                }
                else if (parsedTime.HasValue && parsedTime.Value > nowUtc.AddMinutes(1))
                {
                    values.FailedFields.Add(LinkedFileField.UnlockTime);
                    values.Errors.Add("Unlock time '" + timeCell + "' is in the future.");
                    timeValid = false;
                }
            }

            if (unlockedCell != null)
            {
                if (LinkedFileValueParser.TryParseBoolean(unlockedCell, schema.UnlockedTrueValues, out var unlocked))
                {
                    values.HasUnlockInfo = true;
                    values.Unlocked = unlocked;
                }
                else
                {
                    values.FailedFields.Add(LinkedFileField.Unlocked);
                    values.Errors.Add("Unlocked '" + unlockedCell + "' is not true or false.");
                }
            }
            else if (timeMapped)
            {
                values.HasUnlockInfo = true;
                values.Unlocked = timeValid && parsedTime.HasValue;
            }
            else if (schema.RowPresenceMeansUnlocked)
            {
                values.HasUnlockInfo = true;
                values.Unlocked = true;
            }

            values.UnlockTimeUtc = values.HasUnlockInfo && values.Unlocked && timeValid ? parsedTime : null;

            var numCell = Cell(LinkedFileField.ProgressNum, out _);
            if (numCell != null)
            {
                if (LinkedFileValueParser.TryParseInt(numCell, out var num) && num >= 0)
                {
                    values.ProgressNum = num;
                }
                else
                {
                    values.FailedFields.Add(LinkedFileField.ProgressNum);
                    values.Errors.Add("Progress '" + numCell + "' is not a whole number of 0 or more.");
                }
            }

            var denomCell = Cell(LinkedFileField.ProgressDenom, out _);
            if (denomCell != null)
            {
                if (LinkedFileValueParser.TryParseInt(denomCell, out var denom) && denom >= 0)
                {
                    // A total of 0 is how save files write an achievement without progress.
                    if (denom > 0)
                    {
                        values.ProgressDenom = denom;
                    }
                    else
                    {
                        values.ProgressNum = null;
                    }
                }
                else
                {
                    values.FailedFields.Add(LinkedFileField.ProgressDenom);
                    values.Errors.Add("Progress total '" + denomCell + "' is not a whole number of 0 or more.");
                }
            }

            if (values.ProgressNum.HasValue && values.ProgressDenom.HasValue && values.ProgressNum.Value > values.ProgressDenom.Value)
            {
                values.FailedFields.Add(LinkedFileField.ProgressNum);
                values.Errors.Add("Progress is greater than the progress total.");
            }

            values.DisplayName = Cell(LinkedFileField.DisplayName, out _);
            values.Description = Cell(LinkedFileField.Description, out _);

            var hiddenCell = Cell(LinkedFileField.Hidden, out _);
            if (hiddenCell != null)
            {
                if (LinkedFileValueParser.TryParseBoolean(hiddenCell, null, out var hidden))
                {
                    values.Hidden = hidden;
                }
                else
                {
                    values.FailedFields.Add(LinkedFileField.Hidden);
                    values.Errors.Add("Hidden '" + hiddenCell + "' is not true or false.");
                }
            }

            var pointsCell = Cell(LinkedFileField.Points, out _);
            if (pointsCell != null)
            {
                if (LinkedFileValueParser.TryParseInt(pointsCell, out var points) && points >= 0)
                {
                    values.Points = points;
                }
                else
                {
                    values.FailedFields.Add(LinkedFileField.Points);
                    values.Errors.Add("Points '" + pointsCell + "' is not a whole number of 0 or more.");
                }
            }

            var trophyCell = Cell(LinkedFileField.TrophyType, out _);
            if (trophyCell != null)
            {
                values.TrophyType = CustomAchievementTextImportService.NormalizeTrophyType(trophyCell);
                if (values.TrophyType == null)
                {
                    values.FailedFields.Add(LinkedFileField.TrophyType);
                    values.Errors.Add("Trophy '" + trophyCell + "' " + CustomAchievementTextImportService.TrophyTypeMessage);
                }
            }

            var rarityCell = Cell(LinkedFileField.Rarity, out _);
            if (rarityCell != null)
            {
                if (CustomAchievementTextImportService.TryParseRarity(rarityCell, out var percent, out var tier))
                {
                    values.HasRarity = true;
                    values.RarityPercent = percent;
                    values.RarityTier = tier;
                }
                else
                {
                    values.FailedFields.Add(LinkedFileField.Rarity);
                    values.Errors.Add("Rarity '" + rarityCell + "' " + CustomAchievementTextImportService.RarityMessage);
                }
            }

            values.Category = CustomAchievementTextImportService.ParseCategoryPath(Cell(LinkedFileField.Category, out _));

            var unlockedIconCell = Cell(LinkedFileField.UnlockedIconPath, out _);
            if (unlockedIconCell != null)
            {
                values.UnlockedIconPath = LinkedFileValueParser.ResolveIconSource(unlockedIconCell, fileDirectory);
                if (values.UnlockedIconPath == null)
                {
                    values.FailedFields.Add(LinkedFileField.UnlockedIconPath);
                    values.Errors.Add("Unlocked icon '" + unlockedIconCell + "' is not a web address or a file that exists.");
                }
            }

            var lockedIconCell = Cell(LinkedFileField.LockedIconPath, out _);
            if (lockedIconCell != null)
            {
                values.LockedIconPath = LinkedFileValueParser.ResolveIconSource(lockedIconCell, fileDirectory);
                if (values.LockedIconPath == null)
                {
                    values.FailedFields.Add(LinkedFileField.LockedIconPath);
                    values.Errors.Add("Locked icon '" + lockedIconCell + "' is not a web address or a file that exists.");
                }
            }

            return values;
        }
    }

    /// <summary>
    /// What a file gives an achievement whose state lives in the cache: progress for a
    /// linked-definition achievement, and an unlock or progress for a platform achievement.
    /// </summary>
    public sealed class LinkedFileProgressUpdate
    {
        public string ApiName { get; set; }

        /// <summary>The file unlocks an achievement the cache holds locked.</summary>
        public bool Unlocked { get; set; }

        public DateTime? UnlockTimeUtc { get; set; }

        public int? ProgressNum { get; set; }

        public int? ProgressDenom { get; set; }
    }

    public sealed class LinkedFileApplyReport
    {
        public const int MaxMessages = 50;

        public LinkedFileReadStatus Status { get; set; }

        public DateTime ReadUtc { get; set; }

        public string FilePath { get; set; }

        public int RowCount { get; set; }

        public int MatchedRows { get; set; }

        public int UnmatchedRows { get; set; }

        public int UnmatchedAchievements { get; set; }

        public int Unlocked { get; set; }

        public int Relocked { get; set; }

        public int Updated { get; set; }

        public int Created { get; set; }

        public int ParseErrors { get; set; }

        /// <summary>Diagnostics in English for the log; the window shows the counts.</summary>
        public List<string> Messages { get; } = new List<string>();

        public bool ChangedAnything => Unlocked > 0 || Relocked > 0 || Updated > 0 || Created > 0;

        public void AddMessage(string message)
        {
            if (Messages.Count < MaxMessages)
            {
                Messages.Add(message);
            }
            else if (Messages.Count == MaxMessages)
            {
                Messages.Add("Further messages were dropped.");
            }
        }

        public string Summarize()
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "status={0} rows={1} matched={2} unlocked={3} relocked={4} updated={5} created={6} unmatchedRows={7} unmatchedAchievements={8} errors={9}",
                Status, RowCount, MatchedRows, Unlocked, Relocked, Updated, Created, UnmatchedRows, UnmatchedAchievements, ParseErrors);
        }
    }

    /// <summary>
    /// What applying a file would write: a replacement unlock map for the game's linked
    /// definitions, cache-only progress for those rows, unlocks and progress for the platform's
    /// achievements, and a replacement custom achievement list. The caller writes them; nothing
    /// here touches a store.
    /// </summary>
    public sealed class LinkedFileApplyPlan
    {
        public bool ManualChanged { get; set; }

        /// <summary>The whole unlock map to store on the manual link, keyed by API name.</summary>
        public Dictionary<string, DateTime?> ManualUnlocks { get; } = new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase);

        public List<LinkedFileProgressUpdate> ManualProgress { get; } = new List<LinkedFileProgressUpdate>();

        /// <summary>
        /// Unlocks and progress for achievements a platform provider supplies, added to the cache.
        /// Holds only rows that unlock a locked achievement or carry progress.
        /// </summary>
        public List<LinkedFileProgressUpdate> PlatformUpdates { get; } = new List<LinkedFileProgressUpdate>();

        public bool CustomChanged { get; set; }

        public List<CustomAchievementDefinition> CustomDefinitions { get; } = new List<CustomAchievementDefinition>();

        /// <summary>Indexes of the rows that become new custom achievements.</summary>
        public HashSet<int> CreatedRows { get; } = new HashSet<int>();

        public LinkedFileApplyReport Report { get; } = new LinkedFileApplyReport();
    }

    /// <summary>
    /// Turns a read table into the writes it implies. For custom achievements and linked
    /// definitions the file wins for the rows it contains, locking as well as unlocking. For a
    /// platform's achievements the file only adds: a row unlocks a locked achievement and never
    /// locks one. Achievements with no row keep their state. Definition fields apply to custom
    /// achievements only. Pure: the same inputs always build the same plan.
    /// </summary>
    public static class LinkedFileApplier
    {
        /// <param name="isSameIcon">
        /// Whether a stored icon (first) already holds the file's icon source (second), for icons
        /// that were copied into the game's icon folder. Null compares the text.
        /// </param>
        public static LinkedFileApplyPlan Build(
            LinkedFileTable table,
            LinkedFileSchema schema,
            string fileDirectory,
            IReadOnlyList<AchievementDetail> achievements,
            ManualAchievementLink manualLink,
            IReadOnlyList<CustomAchievementDefinition> customDefinitions,
            DateTime nowUtc,
            Func<string, string, bool> isSameIcon = null,
            IReadOnlyDictionary<string, string> pairings = null)
        {
            var plan = new LinkedFileApplyPlan();
            var report = plan.Report;
            table = table ?? LinkedFileTable.Empty;
            schema = schema ?? new LinkedFileSchema();
            report.ReadUtc = nowUtc;
            report.RowCount = table.Rows.Count;

            var candidates = (achievements ?? Array.Empty<AchievementDetail>())
                .Where(achievement => achievement != null && !string.IsNullOrWhiteSpace(achievement.ApiName))
                .ToList();
            if (candidates.Count == 0 && !schema.CreateUnmatchedRows)
            {
                report.Status = LinkedFileReadStatus.NoAchievements;
                return plan;
            }

            foreach (var mapping in schema.Columns ?? new List<LinkedFileColumnMapping>())
            {
                if (mapping != null && mapping.Field != LinkedFileField.Ignore && table.IndexOfColumn(mapping.Column) < 0)
                {
                    report.AddMessage("The mapped column '" + mapping.Column + "' is not in the file.");
                }
            }

            var match = LinkedFileMatcher.Match(table, schema, candidates, pairings);
            report.MatchedRows = match.Matches.Count;
            report.UnmatchedRows = match.UnmatchedRows.Count;
            report.UnmatchedAchievements = match.UnmatchedAchievements.Count;
            foreach (var diagnostic in match.Diagnostics)
            {
                report.AddMessage(diagnostic);
            }

            // Linked-definition rows start from what the link resolves today, keyed by the
            // current API name, so unmatched rows carry forward and legacy keys migrate.
            var currentManual = new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase);
            if (manualLink != null)
            {
                var resolver = new ManualUnlockResolver(manualLink);
                foreach (var achievement in candidates.Where(candidate => !candidate.IsCustom))
                {
                    if (resolver.TryResolveUnlock(achievement, out _, out var time))
                    {
                        currentManual[achievement.ApiName.Trim()] = time;
                    }
                }
            }

            foreach (var pair in currentManual)
            {
                plan.ManualUnlocks[pair.Key] = pair.Value;
            }

            var originalCustom = (customDefinitions ?? Array.Empty<CustomAchievementDefinition>()).Where(definition => definition != null).ToList();
            plan.CustomDefinitions.AddRange(originalCustom.Select(definition => definition.Clone()));
            var customByApiName = new Dictionary<string, CustomAchievementDefinition>(StringComparer.OrdinalIgnoreCase);
            foreach (var definition in plan.CustomDefinitions)
            {
                var apiName = CustomAchievementProjectionService.BuildApiName(definition.Id);
                if (apiName != null && !customByApiName.ContainsKey(apiName))
                {
                    customByApiName[apiName] = definition;
                }
            }

            var definitionFieldsIgnored = false;
            foreach (var rowMatch in match.Matches)
            {
                var values = LinkedFileRowValues.Parse(table, rowMatch.RowIndex, schema, fileDirectory, nowUtc);
                if (values.Errors.Count > 0)
                {
                    report.ParseErrors++;
                    report.AddMessage(DescribeRow(rowMatch.RowIndex) + ": " + string.Join(" ", values.Errors));
                    continue;
                }

                var achievement = rowMatch.Achievement;
                if (achievement.IsCustom)
                {
                    if (!customByApiName.TryGetValue(achievement.ApiName.Trim(), out var definition))
                    {
                        report.AddMessage(DescribeRow(rowMatch.RowIndex) + ": the custom achievement '" + achievement.ApiName + "' has no definition.");
                        continue;
                    }

                    ApplyToCustom(definition, values, nowUtc, report, isSameIcon);
                }
                else if (manualLink != null)
                {
                    if (values.HasDefinitionFields)
                    {
                        definitionFieldsIgnored = true;
                    }

                    ApplyToManual(achievement, values, currentManual, plan, nowUtc, report);
                }
                else
                {
                    if (values.HasDefinitionFields)
                    {
                        definitionFieldsIgnored = true;
                    }

                    ApplyToPlatform(achievement, values, plan, nowUtc, report);
                }
            }

            if (definitionFieldsIgnored)
            {
                report.AddMessage("Name, description, icon, hidden, points, trophy, rarity and category columns apply to custom achievements only; other achievements keep their source's values.");
            }

            if (schema.CreateUnmatchedRows)
            {
                CreateUnmatched(table, schema, fileDirectory, match, plan, customByApiName, nowUtc, report);
            }

            plan.ManualChanged = manualLink != null && !SameUnlocks(currentManual, plan.ManualUnlocks);
            plan.CustomChanged = !string.Equals(
                JsonConvert.SerializeObject(originalCustom),
                JsonConvert.SerializeObject(plan.CustomDefinitions),
                StringComparison.Ordinal);
            report.Status = LinkedFileReadStatus.Ok;
            return plan;
        }

        private static void ApplyToCustom(
            CustomAchievementDefinition definition,
            LinkedFileRowValues values,
            DateTime nowUtc,
            LinkedFileApplyReport report,
            Func<string, string, bool> isSameIcon)
        {
            bool SameIcon(string stored, string source) =>
                string.Equals(stored, source, StringComparison.OrdinalIgnoreCase) ||
                stored != null && isSameIcon?.Invoke(stored, source) == true;

            var changed = false;
            if (values.HasUnlockInfo)
            {
                var wasUnlocked = definition.Unlocked;
                if (values.Unlocked)
                {
                    var time = values.UnlockTimeUtc ?? (wasUnlocked ? definition.UnlockTimeUtc : nowUtc);
                    if (!wasUnlocked)
                    {
                        report.Unlocked++;
                    }
                    else if (definition.UnlockTimeUtc != time)
                    {
                        changed = true;
                    }

                    definition.Unlocked = true;
                    definition.UnlockTimeUtc = time;
                }
                else
                {
                    if (wasUnlocked)
                    {
                        report.Relocked++;
                    }

                    definition.Unlocked = false;
                    definition.UnlockTimeUtc = null;
                }
            }

            if (values.ProgressNum.HasValue && definition.ProgressNum != values.ProgressNum)
            {
                definition.ProgressNum = values.ProgressNum;
                changed = true;
            }

            if (values.ProgressDenom.HasValue && definition.ProgressDenom != values.ProgressDenom)
            {
                definition.ProgressDenom = values.ProgressDenom;
                changed = true;
            }

            if (values.DisplayName != null && !string.Equals(definition.DisplayName, values.DisplayName, StringComparison.Ordinal))
            {
                definition.DisplayName = values.DisplayName;
                changed = true;
            }

            if (values.Description != null && !string.Equals(definition.Description, values.Description, StringComparison.Ordinal))
            {
                definition.Description = values.Description;
                changed = true;
            }

            if (values.Hidden.HasValue && definition.Hidden != values.Hidden.Value)
            {
                definition.Hidden = values.Hidden.Value;
                changed = true;
            }

            if (values.Points.HasValue && definition.Points != values.Points)
            {
                definition.Points = values.Points;
                changed = true;
            }

            if (values.TrophyType != null && !string.Equals(definition.TrophyType, values.TrophyType, StringComparison.Ordinal))
            {
                definition.TrophyType = values.TrophyType;
                changed = true;
            }

            if (values.HasRarity)
            {
                var rarity = definition.Rarity;
                var percent = definition.GlobalPercentUnlocked;
                CustomAchievementTextImportService.SetRarity(definition, values.RarityPercent, values.RarityTier);
                if (!string.Equals(rarity, definition.Rarity, StringComparison.Ordinal) || percent != definition.GlobalPercentUnlocked)
                {
                    changed = true;
                }
            }

            if (values.Category != null && !string.Equals(definition.Category, values.Category, StringComparison.Ordinal))
            {
                definition.Category = values.Category;
                changed = true;
            }

            if (values.UnlockedIconPath != null && !SameIcon(definition.UnlockedIconPath, values.UnlockedIconPath))
            {
                definition.UnlockedIconPath = values.UnlockedIconPath;
                changed = true;
            }

            if (values.LockedIconPath != null && !SameIcon(definition.LockedIconPath, values.LockedIconPath))
            {
                definition.LockedIconPath = values.LockedIconPath;
                changed = true;
            }

            if (changed)
            {
                report.Updated++;
            }
        }

        /// <summary>
        /// A platform achievement takes an unlock the file adds and progress; a row that says
        /// locked changes nothing, and an achievement already unlocked keeps its time.
        /// </summary>
        private static void ApplyToPlatform(
            AchievementDetail achievement,
            LinkedFileRowValues values,
            LinkedFileApplyPlan plan,
            DateTime nowUtc,
            LinkedFileApplyReport report)
        {
            var unlocks = values.HasUnlockInfo && values.Unlocked && !achievement.Unlocked;
            if (!unlocks && !values.ProgressNum.HasValue && !values.ProgressDenom.HasValue)
            {
                return;
            }

            if (unlocks)
            {
                report.Unlocked++;
            }

            plan.PlatformUpdates.Add(new LinkedFileProgressUpdate
            {
                ApiName = achievement.ApiName.Trim(),
                Unlocked = unlocks,
                UnlockTimeUtc = unlocks ? values.UnlockTimeUtc ?? nowUtc : (DateTime?)null,
                ProgressNum = values.ProgressNum,
                ProgressDenom = values.ProgressDenom
            });
        }

        private static void ApplyToManual(
            AchievementDetail achievement,
            LinkedFileRowValues values,
            Dictionary<string, DateTime?> currentManual,
            LinkedFileApplyPlan plan,
            DateTime nowUtc,
            LinkedFileApplyReport report)
        {
            var apiName = achievement.ApiName.Trim();
            if (values.HasUnlockInfo)
            {
                var wasUnlocked = currentManual.TryGetValue(apiName, out var currentTime);
                if (values.Unlocked)
                {
                    var time = values.UnlockTimeUtc ?? (wasUnlocked ? currentTime : nowUtc);
                    if (!wasUnlocked)
                    {
                        report.Unlocked++;
                    }
                    else if (currentTime != time)
                    {
                        report.Updated++;
                    }

                    plan.ManualUnlocks[apiName] = time;
                }
                else
                {
                    if (wasUnlocked)
                    {
                        report.Relocked++;
                    }

                    plan.ManualUnlocks.Remove(apiName);
                }
            }

            if (values.ProgressNum.HasValue || values.ProgressDenom.HasValue)
            {
                plan.ManualProgress.Add(new LinkedFileProgressUpdate
                {
                    ApiName = apiName,
                    ProgressNum = values.ProgressNum,
                    ProgressDenom = values.ProgressDenom
                });
            }
        }

        private static void CreateUnmatched(
            LinkedFileTable table,
            LinkedFileSchema schema,
            string fileDirectory,
            LinkedFileMatchResult match,
            LinkedFileApplyPlan plan,
            Dictionary<string, CustomAchievementDefinition> customByApiName,
            DateTime nowUtc,
            LinkedFileApplyReport report)
        {
            var usedIds = new HashSet<string>(
                plan.CustomDefinitions.Select(definition => CustomAchievementProjectionService.NormalizeId(definition.Id)).Where(id => id != null),
                StringComparer.OrdinalIgnoreCase);
            // A new achievement takes its API name and name from their columns, and the one the
            // match column is read as from the match column when that field has no column.
            var matchIndex = string.IsNullOrWhiteSpace(schema.MatchColumn) ? -1 : table.IndexOfColumn(schema.MatchColumn);
            var apiNameIndex = schema.TryGetColumn(LinkedFileField.ApiName, out var apiNameColumn)
                ? table.IndexOfColumn(apiNameColumn)
                : schema.MatchBy == LinkedFileMatchBy.ApiName ? matchIndex : -1;
            var nameIndex = !schema.HasColumn(LinkedFileField.DisplayName) && schema.MatchBy == LinkedFileMatchBy.Name ? matchIndex : -1;

            foreach (var rowIndex in match.UnmatchedRows)
            {
                var values = LinkedFileRowValues.Parse(table, rowIndex, schema, fileDirectory, nowUtc);
                if (values.Errors.Count > 0)
                {
                    report.ParseErrors++;
                    report.AddMessage(DescribeRow(rowIndex) + ": " + string.Join(" ", values.Errors));
                    continue;
                }

                var id = apiNameIndex >= 0 ? CustomAchievementProjectionService.NormalizeId(table.Cell(rowIndex, apiNameIndex)) : null;
                if (nameIndex >= 0 && values.DisplayName == null)
                {
                    var name = table.Cell(rowIndex, nameIndex)?.Trim();
                    values.DisplayName = string.IsNullOrEmpty(name) ? null : name;
                }

                if (id == null && values.DisplayName == null)
                {
                    report.AddMessage(DescribeRow(rowIndex) + ": has neither an API name nor a name, so no achievement was created.");
                    continue;
                }

                if (id == null)
                {
                    id = CustomAchievementProjectionService.GenerateId(values.DisplayName, usedIds);
                }
                else if (usedIds.Contains(id))
                {
                    report.AddMessage(DescribeRow(rowIndex) + ": the id '" + id + "' is already used by another achievement.");
                    continue;
                }

                usedIds.Add(id);
                var definition = new CustomAchievementDefinition
                {
                    Id = id,
                    DisplayName = values.DisplayName ?? id,
                    Description = values.Description,
                    Hidden = values.Hidden ?? false,
                    Points = values.Points,
                    TrophyType = values.TrophyType,
                    Category = values.Category,
                    UnlockedIconPath = values.UnlockedIconPath,
                    LockedIconPath = values.LockedIconPath,
                    ProgressNum = values.ProgressNum,
                    ProgressDenom = values.ProgressDenom,
                    Unlocked = values.HasUnlockInfo && values.Unlocked,
                    UnlockTimeUtc = values.HasUnlockInfo && values.Unlocked ? values.UnlockTimeUtc ?? nowUtc : (DateTime?)null
                };

                if (values.HasRarity)
                {
                    CustomAchievementTextImportService.SetRarity(definition, values.RarityPercent, values.RarityTier);
                }

                plan.CustomDefinitions.Add(definition);
                customByApiName[CustomAchievementProjectionService.BuildApiName(id)] = definition;
                report.Created++;
                plan.CreatedRows.Add(rowIndex);
            }
        }

        private static bool SameUnlocks(Dictionary<string, DateTime?> left, Dictionary<string, DateTime?> right)
        {
            if (left.Count != right.Count)
            {
                return false;
            }

            foreach (var pair in left)
            {
                if (!right.TryGetValue(pair.Key, out var time) || time != pair.Value)
                {
                    return false;
                }
            }

            return true;
        }

        private static string DescribeRow(int rowIndex)
        {
            return "Row " + (rowIndex + 1).ToString(CultureInfo.InvariantCulture);
        }
    }
}
