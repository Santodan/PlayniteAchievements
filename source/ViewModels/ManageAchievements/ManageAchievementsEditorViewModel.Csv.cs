using Microsoft.Win32;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Images;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace PlayniteAchievements.ViewModels.ManageAchievements
{
    /// <summary>
    /// The editor's spreadsheet round trip: every row out to a CSV, and a CSV back in as edits to
    /// the rows it names.
    /// </summary>
    public sealed partial class ManageAchievementsEditorViewModel
    {
        private const string CsvFilter = "CSV (*.csv)|*.csv";

        // The game's one Import: a custom-achievements package merges into these rows, a CSV
        // edits them, and a whole-game package replaces the custom data, which the history
        // records as one step.
        private void ImportFile()
        {
            _importPortable?.Invoke(
                MergeImportedDefinitions,
                MergeCsv,
                () => MarkUndoIntent(EditorEditIntent.Atomic("Import", "LOCImportLabel")));
        }

        /// <summary>
        /// Writes every row, provider and authored, as the CSV the import reads back.
        /// </summary>
        private void ExportTemplate()
        {
            var dialog = new SaveFileDialog
            {
                Filter = CsvFilter,
                AddExtension = true,
                DefaultExt = ".csv",
                FileName = BuildCsvFileName(),
                OverwritePrompt = true
            };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            try
            {
                var rows = AchievementRows
                    .Where(row => row != null)
                    .Select(ToCsvRow)
                    .ToList();
                CustomAchievementCsvFormat.WriteFile(
                    dialog.FileName,
                    CustomAchievementCsvFormat.BuildLines(rows, CustomAchievementCsvFormat.SpreadsheetDelimiter()));
                SetStatus(L("LOCPlayAch_Status_Succeeded", "Success!"), false);
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"Failed exporting achievement CSV to '{dialog.FileName}'.");
                SetStatus(string.Format(L("LOCPlayAch_Status_Failed", "Error: {0}"), ex.Message), true);
            }
        }

        private string BuildCsvFileName()
        {
            var gameName = NormalizeText(_gameDataSnapshotProvider?.GetHydratedGameData()?.GameName);
            if (gameName == null)
            {
                return "achievements.csv";
            }

            var invalid = Path.GetInvalidFileNameChars();
            var safe = new string(gameName.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
            return safe + " achievements.csv";
        }

        /// <summary>
        /// What a row shows, as the CSV writes it. A provider row is keyed by its ApiName, an
        /// authored one by its ID.
        /// </summary>
        private static CustomAchievementCsvRow ToCsvRow(AchievementEditorRow row)
        {
            var percent = ParseCsvDouble(row.GlobalPercentUnlockedText);
            return new CustomAchievementCsvRow
            {
                Id = GetCsvKey(row),
                DisplayName = NormalizeText(row.DisplayName),
                Description = NormalizeText(row.Description),
                Points = ParseCsvInt(row.PointsText),
                TrophyType = CustomAchievementTextImportService.NormalizeTrophyType(row.TrophyType),
                Hidden = row.Hidden,
                RarityPercent = percent,
                RarityTier = percent.HasValue
                    ? null
                    : RarityTierExtensions.TryParse(NormalizeText(row.Rarity), out var tier) ? tier.ToString() : null,
                Category = row.EffectiveCategoryLabel,
                ProgressNum = ParseCsvInt(row.ProgressNumText),
                ProgressDenom = ParseCsvInt(row.ProgressDenomText),
                Unlocked = row.Unlocked,
                UnlockTimeUtc = row.Unlocked ? row.UnlockTime : null,
                UnlockedIconPath = OwnIcon(row, AchievementIconVariant.Unlocked),
                LockedIconPath = row.UseSeparateLockedIcons ? OwnIcon(row, AchievementIconVariant.Locked) : null
            };
        }

        /// <summary>
        /// The art the user set in a slot, or null where the slot shows the provider's own: an
        /// export that wrote the provider's cached icons would turn every one of them into an
        /// override when the file came back.
        /// </summary>
        private static string OwnIcon(AchievementEditorRow row, AchievementIconVariant variant)
        {
            var current = NormalizeText(ReadIcon(row, variant));

            // A locked slot can hold a copy of the unlocked icon rather than art of its own;
            // exported, that copy came back as a separate locked file. Same test the override
            // write and the authored save use.
            if (current != null &&
                variant == AchievementIconVariant.Locked &&
                !AchievementIconResolver.HasExplicitLockedIcon(current, row.UnlockedIconPath))
            {
                return null;
            }

            if (current == null || !row.IsProviderRow)
            {
                return current;
            }

            return string.Equals(current, NormalizeText(ReadProviderIcon(row, variant)), StringComparison.OrdinalIgnoreCase)
                ? null
                : current;
        }

        private static string GetCsvKey(AchievementEditorRow row)
        {
            return row.IsProviderRow
                ? NormalizeText(row.OriginalApiName)
                : row.NormalizedId;
        }

        /// <summary>
        /// Applies a CSV to the rows: a filled cell that differs from the row is set through the
        /// same setter an edit uses, a blank cell or a missing column leaves the row alone, and an
        /// ID that names no row adds an authored achievement.
        /// </summary>
        /// <remarks>
        /// All or nothing: any bad value, or a new row without a Title, stops the import before a
        /// single row changes. A value the row cannot take - a provider's rarity, say - is not an
        /// error, only counted, so an unedited export imports cleanly.
        ///
        /// Batched like a selection edit: the provider fields go out as one override write, the
        /// manual unlocks as one link write, the authored rows as one save, and the categories as
        /// one assignment write after the save, so a new row is stored before it is filed.
        /// </remarks>
        private void MergeCsv(string path)
        {
            var parsed = new CustomAchievementTextImportService().Parse(
                File.ReadAllText(path),
                iconBaseDirectory: Path.GetDirectoryName(Path.GetFullPath(path)));
            var errors = new List<string>(parsed.Errors);

            var rowsByKey = new Dictionary<string, AchievementEditorRow>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in AchievementRows)
            {
                var key = row == null ? null : GetCsvKey(row);
                if (key != null && !rowsByKey.ContainsKey(key))
                {
                    rowsByKey[key] = row;
                }
            }

            var matches = new List<(CustomAchievementCsvRow Csv, AchievementEditorRow Row)>();
            foreach (var csv in parsed.Rows)
            {
                AchievementEditorRow row = null;
                if (csv.Id != null)
                {
                    rowsByKey.TryGetValue(csv.Id, out row);
                }

                if (row == null && csv.DisplayName == null)
                {
                    errors.Add($"Row {csv.RowNumber}, ID: \"{csv.Id}\" matches no achievement, so a Title is required.");
                }

                matches.Add((csv, row));
            }

            if (errors.Count > 0)
            {
                SetStatus(string.Join(Environment.NewLine, errors.Take(8)), true);
                return;
            }

            if (matches.Count == 0)
            {
                SetStatus(L("LOCPlayAch_ManageAchievements_Custom_NoImportRows", "No importable achievements were found."), true);
                return;
            }

            MarkUndoIntent(EditorEditIntent.Command("ImportCsv", "LOCImportLabel"));

            var outcome = new CsvMergeOutcome();
            var usedIds = new HashSet<string>(
                AchievementRows
                    .Select(row => row?.NormalizedId)
                    .Concat(matches.Select(match => match.Csv.Id))
                    .Where(id => !string.IsNullOrWhiteSpace(id)),
                StringComparer.OrdinalIgnoreCase);
            var useSeparateLockedIcons = ResolveUseSeparateLockedIcons();

            var previousApplyingBulk = _isApplyingBulk;
            _isImportingCsv = true;
            _isApplyingBulk = true;
            _isTogglingReveal = true;
            _batchedFieldWrites = new List<(string, AchievementEditableField, object)>();
            _batchedNoteWrites = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var (csv, row) in matches)
                {
                    if (row != null)
                    {
                        ApplyCsvRow(row, csv, outcome);
                        continue;
                    }

                    var id = csv.Id ?? CustomAchievementProjectionService.GenerateId(csv.DisplayName, usedIds);
                    usedIds.Add(id);
                    var added = AchievementEditorRow.FromDefinition(CustomAchievementTextImportService.ToDefinition(csv, id));
                    added.MarkAsNewImport();
                    AttachRow(added, useSeparateLockedIcons);
                    AchievementRows.Add(added);
                    outcome.Added.Add(added);
                    outcome.TouchedAuthored = true;
                    if (csv.Category != null)
                    {
                        added.CategoryLabel = ResolveImportedCategory(csv.Category, outcome);
                        outcome.TouchedCategory = true;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed importing a CSV for gameId={_gameId}.");
                SetStatus(string.Format(L("LOCPlayAch_Status_Failed", "Error: {0}"), ex.Message), true);
            }
            finally
            {
                try
                {
                    FlushBatchedFieldWrites();
                }
                finally
                {
                    _isApplyingBulk = previousApplyingBulk;
                    _isTogglingReveal = false;
                }
            }

            RefreshRevealHeaderState();

            if (outcome.TouchedManualUnlocks)
            {
                StageManualUnlocks();
                FlushManualUnlocks();
            }

            if (outcome.Added.Count > 0)
            {
                SelectedRow = outcome.Added[outcome.Added.Count - 1];
            }

            var summary = BuildCsvImportSummary(parsed, matches.Count, outcome);
            SetStatus(summary, false);
            _ = FinishCsvImportAsync(outcome, summary);
        }

        /// <summary>
        /// The writes that wait on files: icons fetched and stored, then the authored rows saved,
        /// then the categories filed, so a new row exists before it is assigned.
        /// </summary>
        /// <remarks>
        /// <see cref="_isImportingCsv"/> stays set until the last of them, which keeps the icon
        /// writes from naming steps of their own and the step itself open, so the copies of any
        /// art written over can be attached to it.
        /// </remarks>
        private async Task FinishCsvImportAsync(CsvMergeOutcome outcome, string summary)
        {
            try
            {
                if (outcome.UnlockedIconRows.Count > 0)
                {
                    await ApplyIconEditAsync(outcome.UnlockedIconRows, AchievementIconVariant.Unlocked);
                }

                if (outcome.LockedIconRows.Count > 0)
                {
                    await ApplyIconEditAsync(outcome.LockedIconRows, AchievementIconVariant.Locked);
                }

                if (outcome.TouchedAuthored)
                {
                    RefreshComputedState();
                    await SaveAsync();
                }

                if (outcome.TouchedCategory)
                {
                    PersistCategoryAssignmentsFromRows();
                }

                foreach (var restore in outcome.ArtRestores)
                {
                    _undoJournal.RecordArtRestore(restore.ApiName, restore.PropertyName, restore.OldValue, restore.NewValue);
                }

                // An icon that failed to download reports itself; otherwise the summary stands.
                if (!_statusIsError)
                {
                    SetStatus(summary, false);
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed saving a CSV import for gameId={_gameId}.");
                SetStatus(string.Format(L("LOCPlayAch_Status_Failed", "Error: {0}"), ex.Message), true);
            }
            finally
            {
                _isImportingCsv = false;
                RaiseHistoryState();
            }
        }

        /// <summary>
        /// Sets each filled cell that differs from the row, where the row lets that field be
        /// edited, and persists a provider row's fields into the open batch.
        /// </summary>
        private void ApplyCsvRow(AchievementEditorRow row, CustomAchievementCsvRow csv, CsvMergeOutcome outcome)
        {
            var changed = false;

            void Set(string propertyName, Action apply)
            {
                apply();
                changed = true;
                if (row.IsProviderRow)
                {
                    PersistProviderRowField(row, propertyName);
                }
                else
                {
                    outcome.TouchedAuthored = true;
                }
            }

            if (csv.DisplayName != null && !string.Equals(csv.DisplayName, NormalizeText(row.DisplayName), StringComparison.Ordinal))
            {
                Set(nameof(AchievementEditorRow.DisplayName), () => row.DisplayName = csv.DisplayName);
            }

            if (csv.Description != null && !string.Equals(csv.Description, NormalizeText(row.Description), StringComparison.Ordinal))
            {
                Set(nameof(AchievementEditorRow.Description), () => row.Description = csv.Description);
            }

            if (csv.Points.HasValue && csv.Points != ParseCsvInt(row.PointsText))
            {
                Set(
                    nameof(AchievementEditorRow.PointsText),
                    () => row.PointsText = csv.Points.Value.ToString(CultureInfo.InvariantCulture));
            }

            if (csv.TrophyType != null &&
                !string.Equals(csv.TrophyType, CustomAchievementTextImportService.NormalizeTrophyType(row.TrophyType), StringComparison.Ordinal))
            {
                Set(nameof(AchievementEditorRow.TrophyType), () => row.TrophyType = csv.TrophyType);
            }

            if (csv.Hidden.HasValue && csv.Hidden.Value != row.Hidden)
            {
                Set(nameof(AchievementEditorRow.Hidden), () => row.Hidden = csv.Hidden.Value);
            }

            if (csv.RarityText != null &&
                !string.Equals(csv.RarityText, ToCsvRow(row).RarityText, StringComparison.OrdinalIgnoreCase))
            {
                if (row.CanEditRarity)
                {
                    Set(nameof(AchievementEditorRow.RarityInput), () => row.RarityInput = csv.RarityText);
                }
                else
                {
                    outcome.Skipped++;
                }
            }

            if (csv.ProgressNum.HasValue && csv.ProgressNum != ParseCsvInt(row.ProgressNumText))
            {
                if (row.CanEditProgress)
                {
                    Set(
                        nameof(AchievementEditorRow.ProgressNumText),
                        () => row.ProgressNumText = csv.ProgressNum.Value.ToString(CultureInfo.InvariantCulture));
                }
                else
                {
                    outcome.Skipped++;
                }
            }

            if (csv.ProgressDenom.HasValue && csv.ProgressDenom != ParseCsvInt(row.ProgressDenomText))
            {
                if (row.CanEditProgress)
                {
                    Set(
                        nameof(AchievementEditorRow.ProgressDenomText),
                        () => row.ProgressDenomText = csv.ProgressDenom.Value.ToString(CultureInfo.InvariantCulture));
                }
                else
                {
                    outcome.Skipped++;
                }
            }

            ApplyCsvUnlock(row, csv, outcome, ref changed);

            if (csv.Category != null && !CategoryPathHelper.IsSame(csv.Category, row.EffectiveCategoryLabel))
            {
                if (row.CanEditAssignments)
                {
                    row.CategoryLabel = ResolveImportedCategory(csv.Category, outcome);
                    outcome.TouchedCategory = true;
                    changed = true;
                }
                else
                {
                    outcome.Skipped++;
                }
            }

            ApplyCsvIcon(row, csv.UnlockedIconPath, AchievementIconVariant.Unlocked, outcome, ref changed);
            ApplyCsvIcon(row, csv.LockedIconPath, AchievementIconVariant.Locked, outcome, ref changed);

            if (changed)
            {
                outcome.Updated++;
            }
        }

        /// <summary>
        /// Puts an icon cell into the row's slot, where the editor's icon edit then fetches and
        /// stores it.
        /// </summary>
        /// <remarks>
        /// A locked icon only counts on a game set to show separate locked art; elsewhere the
        /// locked look is drawn from the unlocked icon and the cell is skipped. Art the slot holds
        /// of the user's own is copied first, because storing the new icon writes over that file.
        /// </remarks>
        private void ApplyCsvIcon(
            AchievementEditorRow row,
            string source,
            AchievementIconVariant variant,
            CsvMergeOutcome outcome,
            ref bool changed)
        {
            if (source == null)
            {
                return;
            }

            var current = NormalizeText(ReadIcon(row, variant));
            if (string.Equals(source, current, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (variant == AchievementIconVariant.Locked && !row.UseSeparateLockedIcons)
            {
                outcome.Skipped++;
                return;
            }

            var own = OwnIcon(row, variant);
            if (own != null && !string.IsNullOrWhiteSpace(row.OriginalApiName))
            {
                outcome.ArtRestores.Add(new EditorRowValueChange(
                    row.OriginalApiName,
                    variant == AchievementIconVariant.Locked
                        ? nameof(AchievementEditorRow.LockedIconPath)
                        : nameof(AchievementEditorRow.UnlockedIconPath),
                    RetainIconValue(own),
                    RetainIconValue(source)));
            }

            WriteIcon(row, variant, source);
            changed = true;
            if (row.IsProviderRow)
            {
                (variant == AchievementIconVariant.Locked ? outcome.LockedIconRows : outcome.UnlockedIconRows).Add(row);
            }
            else
            {
                outcome.TouchedAuthored = true;
            }
        }

        /// <summary>
        /// Unlock state where the row owns it: an authored row, or any row of a manually tracked
        /// game. A provider row elsewhere only takes a corrected time on an achievement that is
        /// already unlocked, which is what the editor allows.
        /// </summary>
        private void ApplyCsvUnlock(
            AchievementEditorRow row,
            CustomAchievementCsvRow csv,
            CsvMergeOutcome outcome,
            ref bool changed)
        {
            var unlockedDiffers = csv.Unlocked.HasValue && csv.Unlocked.Value != row.Unlocked;
            var timeDiffers = csv.UnlockTimeUtc.HasValue &&
                TruncateToSecond(csv.UnlockTimeUtc) != TruncateToSecond(row.Unlocked ? row.UnlockTime : null);
            if (!unlockedDiffers && !timeDiffers)
            {
                return;
            }

            if (row.CanEditUnlocked)
            {
                if (unlockedDiffers)
                {
                    row.Unlocked = csv.Unlocked.Value;
                }

                if (timeDiffers)
                {
                    row.UnlockTime = csv.UnlockTimeUtc;
                }

                changed = true;
                if (row.IsProviderRow)
                {
                    outcome.TouchedManualUnlocks = true;
                }
                else
                {
                    outcome.TouchedAuthored = true;
                }

                return;
            }

            if (unlockedDiffers)
            {
                outcome.Skipped++;
            }

            if (timeDiffers)
            {
                if (row.Unlocked && csv.Unlocked != false)
                {
                    row.UnlockTime = csv.UnlockTimeUtc;
                    PersistProviderRowField(row, nameof(AchievementEditorRow.UnlockTime));
                    changed = true;
                }
                else
                {
                    outcome.Skipped++;
                }
            }
        }

        /// <summary>
        /// The category a CSV path files a row under: an existing one when the path matches it in
        /// any case, otherwise a new one, with any missing parents, staged into the order write
        /// that rides along with the assignment.
        /// </summary>
        private string ResolveImportedCategory(string path, CsvMergeOutcome outcome)
        {
            string resolved = null;
            foreach (var segment in CategoryPathHelper.Split(path))
            {
                var candidate = CategoryPathHelper.Join(resolved, segment);
                var existing = AssignableCategoryOptions
                    .Concat(_pendingCategoryOrderWrite ?? Enumerable.Empty<string>())
                    .FirstOrDefault(option => CategoryPathHelper.IsSame(option, candidate));
                if (existing != null)
                {
                    resolved = existing;
                    continue;
                }

                if (_pendingCategoryOrderWrite == null)
                {
                    // The whole known set, as CreateAndAssignCategory builds it: a partial order
                    // would pin the new category ahead of ones that never needed an entry.
                    _pendingCategoryOrderWrite = AssignableCategoryOptions
                        .Where(option => !string.IsNullOrWhiteSpace(option))
                        .ToList();
                }

                _pendingCategoryOrderWrite.Add(candidate);
                outcome.CreatedCategories.Add(CategoryPathHelper.ToDisplayPath(candidate));
                resolved = candidate;
            }

            return resolved;
        }

        private string BuildCsvImportSummary(CustomAchievementCsvParseResult parsed, int rowCount, CsvMergeOutcome outcome)
        {
            var lines = new List<string>
            {
                string.Format(
                    L("LOCPlayAch_ManageAchievements_Custom_ImportSummary", "Imported {0} rows ({1} added, {2} updated)."),
                    rowCount,
                    outcome.Added.Count,
                    outcome.Updated)
            };

            if (outcome.Skipped > 0)
            {
                lines.Add(string.Format(
                    L("LOCPlayAch_ManageAchievements_Custom_ImportSkippedValues", "{0} values were skipped because those achievements don't allow editing them."),
                    outcome.Skipped));
            }

            if (outcome.CreatedCategories.Count > 0)
            {
                lines.Add(string.Format(
                    L("LOCPlayAch_ManageAchievements_Custom_ImportCreatedCategories", "Created categories: {0}"),
                    string.Join(", ", outcome.CreatedCategories)));
            }

            if (parsed.IgnoredColumns.Count > 0)
            {
                lines.Add(string.Format(
                    L("LOCPlayAch_ManageAchievements_Custom_ImportIgnoredColumns", "Ignored columns: {0}"),
                    string.Join(", ", parsed.IgnoredColumns)));
            }

            return string.Join(Environment.NewLine, lines);
        }

        private static int? ParseCsvInt(string value)
        {
            var normalized = NormalizeText(value);
            return normalized != null &&
                   int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : (int?)null;
        }

        private static double? ParseCsvDouble(string value)
        {
            var normalized = NormalizeText(value)?.TrimEnd('%').Trim();
            return !string.IsNullOrEmpty(normalized) &&
                   double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : (double?)null;
        }

        private static DateTime? TruncateToSecond(DateTime? value)
        {
            if (!value.HasValue)
            {
                return null;
            }

            var utc = value.Value.Kind == DateTimeKind.Local ? value.Value.ToUniversalTime() : value.Value;
            return new DateTime(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
        }

        private sealed class CsvMergeOutcome
        {
            public List<AchievementEditorRow> Added { get; } = new List<AchievementEditorRow>();

            public List<string> CreatedCategories { get; } = new List<string>();

            public List<AchievementEditorRow> UnlockedIconRows { get; } = new List<AchievementEditorRow>();

            public List<AchievementEditorRow> LockedIconRows { get; } = new List<AchievementEditorRow>();

            public List<EditorRowValueChange> ArtRestores { get; } = new List<EditorRowValueChange>();

            public int Updated { get; set; }

            public int Skipped { get; set; }

            public bool TouchedAuthored { get; set; }

            public bool TouchedCategory { get; set; }

            public bool TouchedManualUnlocks { get; set; }
        }
    }
}
