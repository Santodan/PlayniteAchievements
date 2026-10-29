using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Hydration;
using PlayniteAchievements.Services.Images;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PlayniteAchievements.Services.Workshop.Preview
{
    /// <summary>
    /// Works out what installing a game data package would change on one library game. The
    /// package's record is built the way the installer builds it (including the update merge
    /// against the previous install's baseline), then run through the same achievement hydration
    /// the game's live rows go through, on a copy of the game's raw achievements. The result is
    /// compared row by row with the game's current rows. Reads files only; safe off the UI thread.
    /// </summary>
    public static class GameCustomDataPreviewDiffBuilder
    {
        /// <summary>
        /// The diff for <paramref name="package"/> against <paramref name="source"/>, or with a
        /// null source a package-only listing of what the package carries.
        /// </summary>
        public static GameCustomDataPreviewDiff Build(
            GameCustomDataPortablePackage package,
            GameCustomDataPreviewSource source)
        {
            if (package == null)
            {
                throw new ArgumentNullException(nameof(package));
            }

            // The summary counts what the package carries; the comparison and the touched rows use
            // its keys as the install lays them over the compared game's own scopes.
            var entries = BuildPackageEntries(package);
            var diff = new GameCustomDataPreviewDiff();
            FillSummary(diff, package, entries);

            if (source == null)
            {
                diff.PackageTouchedApiNames = BuildTouchedApiNames(package, entries);
                diff.IsPackageOnly = true;
                diff.Rows = entries
                    .Select(entry => new AchievementPreviewRow(entry.ApiName, null, entry.State, entry.Changes))
                    .ToList();
                return diff;
            }

            var scoped = ScopeToGame(package, source);
            diff.PackageTouchedApiNames = BuildTouchedApiNames(scoped, BuildPackageEntries(scoped));

            var predicted = PredictRecord(scoped, source, out var keptEdits, out var hasBaseline);
            diff.ComparedAgainstGameName = source.GameName;
            diff.KeptEditCount = keptEdits;
            diff.HasBaseline = hasBaseline;
            diff.OrderChanged = !SameOrder(predicted?.AchievementOrder, source.Current?.AchievementOrder);

            var afterData = HydratePredicted(predicted, source);
            diff.AfterData = afterData;
            diff.BeforeData = source.CurrentData;

            // Hydration stamps each row's position under the custom order.
            var after = afterData.Achievements
                .Where(achievement => achievement != null)
                .OrderBy(achievement => achievement.DefaultOrderIndex)
                .ToList();
            var before = source.CurrentData?.Achievements ?? new List<AchievementDetail>();
            CompareRows(diff, before, after);
            return diff;
        }

        // ---- the record the install would leave --------------------------------------------

        /// <summary>
        /// The package with its manifest keys spread over the compared game's scopes, as
        /// <c>GameCustomDataStore.ImportReplacePortable</c> does before it saves. The package
        /// itself is left as read; other shapes come back unchanged.
        /// </summary>
        private static GameCustomDataPortablePackage ScopeToGame(
            GameCustomDataPortablePackage package,
            GameCustomDataPreviewSource source)
        {
            var target = source.CurrentData ?? source.RawData;
            var scope = AchievementKeyScopes.For(target?.ProviderKey);
            if (package.Shape != GameCustomDataPackageShape.Manifest || package.Manifest == null || scope == null)
            {
                return package;
            }

            var manifest = package.Manifest.Clone();
            GameCustomDataPortableScoping.ToGame(manifest, scope, target.Achievements);
            return new GameCustomDataPortablePackage(
                package.Shape,
                manifest,
                package.CustomAchievements,
                package.ExtractedImages,
                package.ImageOnlyEntries);
        }

        /// <summary>
        /// The custom data record the install would store for the game: the package's record
        /// built as <c>WorkshopInstaller.InstallGameData</c> and the store's import build it,
        /// merged with the baseline when the game was installed from this item before.
        /// </summary>
        internal static GameCustomDataFile PredictRecord(
            GameCustomDataPortablePackage package,
            GameCustomDataPreviewSource source,
            out int keptEdits,
            out bool hasBaseline)
        {
            keptEdits = 0;
            hasBaseline = false;
            var gameId = source.GameId;
            var current = source.Current;

            if (package.Shape == GameCustomDataPackageShape.CustomAchievementsCsv)
            {
                // Merged by ID into the existing record; the installer runs no baseline merge here.
                var record = current?.Clone() ?? new GameCustomDataFile { PlayniteGameId = gameId };
                record.CustomAchievements = CustomAchievementProjectionService.MergeDefinitionsById(
                    record.CustomAchievements,
                    package.CustomAchievements?.Definitions,
                    out _,
                    out _);
                return GameCustomDataNormalizer.NormalizeInternal(record, gameId);
            }

            var portable = package.Shape == GameCustomDataPackageShape.ImageOnly
                ? BuildImageOnlyPortable(package, source.CurrentData)
                : package.Manifest;

            // The store's replace import: the package record with the target's exclusions and the
            // user's own progress carried over, normalized as a save would.
            var incoming = GameCustomDataFile.FromPortable(
                portable,
                gameId,
                current?.ExcludedFromRefreshes,
                current?.ExcludedFromSummaries);
            PortablePersonalState.CarryLocal(current, incoming);
            incoming = GameCustomDataNormalizer.NormalizeInternal(incoming, gameId);

            if (source.Baseline == null || current == null)
            {
                return incoming;
            }

            hasBaseline = true;
            var merged = GameCustomDataThreeWayMerge.Merge(source.Baseline, current, incoming, out keptEdits);
            return keptEdits > 0
                ? GameCustomDataNormalizer.NormalizeInternal(merged, gameId)
                : incoming;
        }

        /// <summary>
        /// The portable record an image-only import builds: one icon override per package image
        /// whose name matches one of the game's achievements, the rest ignored.
        /// </summary>
        private static GameCustomDataPortableFile BuildImageOnlyPortable(
            GameCustomDataPortablePackage package,
            GameAchievementData currentData)
        {
            var apiNames = new HashSet<string>(
                (currentData?.Achievements ?? new List<AchievementDetail>())
                    .Select(achievement => Normalize(achievement?.ApiName))
                    .Where(apiName => apiName != null),
                StringComparer.OrdinalIgnoreCase);
            if (apiNames.Count == 0)
            {
                throw new InvalidOperationException("Image-only .PA.ZIP imports require cached achievements for the target game.");
            }

            var portable = new GameCustomDataPortableFile();
            foreach (var entry in package.ImageOnlyEntries)
            {
                var apiName = Normalize(entry?.ApiName);
                if (apiName == null || !apiNames.Contains(apiName) || string.IsNullOrWhiteSpace(entry.Path))
                {
                    continue;
                }

                portable.AchievementOverrides ??= new Dictionary<string, AchievementOverride>(StringComparer.OrdinalIgnoreCase);
                if (!portable.AchievementOverrides.TryGetValue(apiName, out var record))
                {
                    record = new AchievementOverride();
                    portable.AchievementOverrides[apiName] = record;
                }

                if (entry.Variant == AchievementIconVariant.Locked)
                {
                    portable.AchievementLockedIconOverrides ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    portable.AchievementLockedIconOverrides[apiName] = entry.Path;
                    record.LockedIconPath = entry.Path;
                }
                else
                {
                    portable.AchievementUnlockedIconOverrides ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    portable.AchievementUnlockedIconOverrides[apiName] = entry.Path;
                    record.UnlockedIconPath = entry.Path;
                }
            }

            if (portable.AchievementOverrides == null)
            {
                throw new InvalidOperationException("Image-only .PA.ZIP did not contain any images matching this game's achievement API names.");
            }

            return portable;
        }

        // ---- after rows ---------------------------------------------------------------------

        /// <summary>
        /// The game as it would show with <paramref name="predicted"/> stored: a copy of the raw
        /// achievements through the hydration pipeline, with the game-level fields
        /// <c>GameDataHydrator.Hydrate</c> sets taken from the predicted record.
        /// </summary>
        private static GameAchievementData HydratePredicted(
            GameCustomDataFile predicted,
            GameCustomDataPreviewSource source)
        {
            var data = CopyForHydration(source.RawData, source.GameId);

            var resolved = GameCustomDataLookup.BuildResolvedFromRecord(predicted, source.Persisted);
            data.Game = source.CurrentData?.Game;
            data.ExcludedByUser = resolved.ExcludedFromRefreshes;
            data.ExcludedFromSummaries = resolved.ExcludedFromSummaries;
            data.UseSeparateLockedIconsWhenAvailable = resolved.UseSeparateLockedIcons;
            data.AchievementOrder = resolved.AchievementOrder.Count > 0 ? new List<string>(resolved.AchievementOrder) : null;
            data.GoalAchievements = resolved.GoalAchievementApiNames.Count > 0 ? new List<string>(resolved.GoalAchievementApiNames) : null;
            data.AchievementCategoryOrder = resolved.AchievementCategoryOrder.Count > 0 ? new List<string>(resolved.AchievementCategoryOrder) : null;
            data.AchievementCategoryImageOverrides = resolved.AchievementCategoryImageOverrides.Count > 0
                ? AchievementIconOverrideHelper.ResolveCategoryImageOverrides(
                    resolved.AchievementCategoryImageOverrides,
                    source.GameId,
                    source.ManagedCustomIconService)
                : null;
            data.GameSummaryCategory = resolved.GameSummaryCategory;

            AchievementOverlayPipeline.Apply(
                data,
                source.GameId,
                resolved,
                source.ManagedCustomIconService,
                () => predicted.AchievementUnlockedIconOverrides,
                () => predicted.AchievementLockedIconOverrides);
            data.Achievements ??= new List<AchievementDetail>();

            // After hydration, so custom achievements projected from the record are covered too.
            if (source.HidePersonalProgress)
            {
                foreach (var achievement in data.Achievements.Where(achievement => achievement != null))
                {
                    achievement.UnlockTimeUtc = null;
                    achievement.Unlocked = true;
                    achievement.ProgressNum = null;
                    achievement.ProgressDenom = null;
                }
            }

            return data;
        }

        /// <summary>
        /// A copy of the raw data deep enough that hydration cannot reach the source: the game
        /// fields hydration reads and a fresh instance of every achievement.
        /// </summary>
        private static GameAchievementData CopyForHydration(GameAchievementData raw, Guid gameId)
        {
            return new GameAchievementData
            {
                PlayniteGameId = gameId,
                GameName = raw?.GameName,
                ProviderKey = raw?.ProviderKey ?? CustomAchievementProjectionService.ProviderKey,
                ProviderPlatformKey = raw?.ProviderPlatformKey,
                LibrarySourceName = raw?.LibrarySourceName,
                RaritySource = raw?.RaritySource,
                PlatformFields = raw?.PlatformFields,
                MetadataFields = raw?.MetadataFields,
                HasAchievements = raw?.HasAchievements ?? true,
                Achievements = (raw?.Achievements ?? new List<AchievementDetail>())
                    .Where(achievement => achievement != null)
                    .Select(CopyDetail)
                    .ToList()
            };
        }

        private static AchievementDetail CopyDetail(AchievementDetail source)
        {
            return new AchievementDetail
            {
                ApiName = source.ApiName,
                DisplayName = source.DisplayName,
                Description = source.Description,
                UnlockedIconPath = source.UnlockedIconPath,
                LockedIconPath = source.LockedIconPath,
                Points = source.Points,
                ScaledPoints = source.ScaledPoints,
                Tags = source.Tags,
                Category = source.Category,
                ProviderCategory = source.ProviderCategory,
                TrophyType = source.TrophyType,
                IsCapstone = source.IsCapstone,
                IsCustom = source.IsCustom,
                ProviderKey = source.ProviderKey,
                Hidden = source.Hidden,
                Unlocked = source.Unlocked,
                UnlockTimeUtc = source.UnlockTimeUtc,
                GlobalPercentUnlocked = source.GlobalPercentUnlocked,
                Rarity = source.Rarity,
                ProgressNum = source.ProgressNum,
                ProgressDenom = source.ProgressDenom
            };
        }

        // ---- comparison ---------------------------------------------------------------------

        private static void CompareRows(
            GameCustomDataPreviewDiff diff,
            IReadOnlyList<AchievementDetail> before,
            IReadOnlyList<AchievementDetail> after)
        {
            var beforeByApiName = new Dictionary<string, AchievementDetail>(StringComparer.OrdinalIgnoreCase);
            foreach (var achievement in before)
            {
                var apiName = Normalize(achievement?.ApiName);
                if (apiName != null && !beforeByApiName.ContainsKey(apiName))
                {
                    beforeByApiName[apiName] = achievement;
                }
            }

            var images = new ImageComparer();
            var rows = new List<AchievementPreviewRow>();
            var unchangedRows = new List<AchievementPreviewRow>();
            var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var achievement in after)
            {
                var apiName = Normalize(achievement.ApiName);
                if (apiName == null || !matched.Add(apiName))
                {
                    continue;
                }

                var afterState = ToState(achievement);
                if (!beforeByApiName.TryGetValue(apiName, out var previous))
                {
                    rows.Add(new AchievementPreviewRow(apiName, null, afterState, AchievementPreviewChange.Added));
                    continue;
                }

                var beforeState = ToState(previous);
                var changes = Compare(beforeState, afterState, images);
                if (changes == AchievementPreviewChange.None)
                {
                    unchangedRows.Add(new AchievementPreviewRow(apiName, beforeState, afterState, AchievementPreviewChange.None));
                    continue;
                }

                rows.Add(new AchievementPreviewRow(apiName, beforeState, afterState, changes));
            }

            foreach (var pair in beforeByApiName.Where(pair => !matched.Contains(pair.Key)))
            {
                rows.Add(new AchievementPreviewRow(pair.Key, ToState(pair.Value), null, AchievementPreviewChange.Removed));
            }

            diff.Rows = rows;
            diff.UnchangedRows = unchangedRows;
            diff.UnchangedCount = unchangedRows.Count;
        }

        private static AchievementPreviewChange Compare(
            AchievementPreviewState before,
            AchievementPreviewState after,
            ImageComparer images)
        {
            var changes = AchievementPreviewChange.None;
            if (!SameText(before.DisplayName, after.DisplayName)) changes |= AchievementPreviewChange.Name;
            if (!SameText(before.Description, after.Description)) changes |= AchievementPreviewChange.Description;
            if (!images.Same(before.UnlockedIcon as string, after.UnlockedIcon as string)) changes |= AchievementPreviewChange.UnlockedIcon;
            if (!images.Same(before.LockedIcon as string, after.LockedIcon as string)) changes |= AchievementPreviewChange.LockedIcon;
            if (!SameText(before.Category, after.Category)) changes |= AchievementPreviewChange.Category;
            if (!SameText(before.Tags, after.Tags)) changes |= AchievementPreviewChange.Tags;
            if (before.IsCapstone != after.IsCapstone) changes |= AchievementPreviewChange.Capstone;
            if (!SameText(before.Note, after.Note)) changes |= AchievementPreviewChange.Note;
            if (before.IsFiltered != after.IsFiltered) changes |= AchievementPreviewChange.Filter;
            if (before.IsGoal != after.IsGoal) changes |= AchievementPreviewChange.Goal;
            return changes;
        }

        private static AchievementPreviewState ToState(AchievementDetail achievement)
        {
            return new AchievementPreviewState
            {
                DisplayName = achievement.DisplayName,
                Description = achievement.Description,
                UnlockedIcon = Normalize(achievement.UnlockedIconPath),
                LockedIcon = Normalize(achievement.LockedIconPath),
                Category = achievement.Category,
                Tags = AchievementTagHelper.Normalize(achievement.Tags),
                IsCapstone = achievement.IsCapstone,
                IsGoal = achievement.IsGoal,
                IsFiltered = achievement.IsFiltered,
                Note = achievement.AchievementNote,
                IsCustom = achievement.IsCustom
            };
        }

        private static bool SameOrder(IReadOnlyList<string> left, IReadOnlyList<string> right)
        {
            var a = left ?? Array.Empty<string>();
            var b = right ?? Array.Empty<string>();
            return a.Count == b.Count &&
                   a.Zip(b, (x, y) => string.Equals(x, y, StringComparison.OrdinalIgnoreCase)).All(same => same);
        }

        private static bool SameText(string left, string right)
        {
            return string.Equals(Normalize(left), Normalize(right), StringComparison.Ordinal);
        }

        /// <summary>
        /// Two icon paths show the same image when they are the same path or both files exist with
        /// the same content, so a reinstall that rewrites an icon byte for byte is not a change.
        /// </summary>
        private sealed class ImageComparer
        {
            private readonly Dictionary<string, string> _hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            public bool Same(string left, string right)
            {
                left = Normalize(left);
                right = Normalize(right);
                if (left == null || right == null)
                {
                    return left == right;
                }

                if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                var leftHash = Hash(left);
                return leftHash != null && string.Equals(leftHash, Hash(right), StringComparison.Ordinal);
            }

            private string Hash(string path)
            {
                if (_hashes.TryGetValue(path, out var hash))
                {
                    return hash;
                }

                try
                {
                    hash = File.Exists(path) ? WorkshopBaselineStore.HashFile(path) : null;
                }
                catch (IOException)
                {
                    hash = null;
                }
                catch (UnauthorizedAccessException)
                {
                    hash = null;
                }

                _hashes[path] = hash;
                return hash;
            }
        }

        // ---- package-only listing and summary -----------------------------------------------

        private sealed class PackageEntry
        {
            public string ApiName { get; set; }
            public AchievementPreviewState State { get; } = new AchievementPreviewState();
            public AchievementPreviewChange Changes { get; set; }
        }

        /// <summary>
        /// One entry per achievement the package touches, with only what the package sets: the
        /// per-achievement overrides, icon maps, capstones and filters of a manifest, each custom
        /// achievement, or the icons of an image-only package. Manifest entries follow the
        /// package's achievement order where it names them.
        /// </summary>
        private static List<PackageEntry> BuildPackageEntries(GameCustomDataPortablePackage package)
        {
            var entries = new Dictionary<string, PackageEntry>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();

            PackageEntry Entry(string apiName)
            {
                apiName = Normalize(apiName);
                if (apiName == null)
                {
                    return null;
                }

                if (!entries.TryGetValue(apiName, out var entry))
                {
                    entry = new PackageEntry { ApiName = apiName };
                    entries[apiName] = entry;
                    order.Add(apiName);
                }

                return entry;
            }

            switch (package.Shape)
            {
                case GameCustomDataPackageShape.Manifest:
                    AddManifestEntries(package.Manifest, Entry);
                    break;
                case GameCustomDataPackageShape.CustomAchievementsCsv:
                    AddCustomAchievementEntries(package.CustomAchievements?.Definitions, Entry);
                    break;
                case GameCustomDataPackageShape.ImageOnly:
                    foreach (var image in package.ImageOnlyEntries)
                    {
                        var entry = Entry(image?.ApiName);
                        if (entry == null)
                        {
                            continue;
                        }

                        if (image.Variant == AchievementIconVariant.Locked)
                        {
                            SetLockedIcon(entry, image.Path);
                        }
                        else
                        {
                            SetUnlockedIcon(entry, image.Path);
                        }
                    }

                    break;
            }

            var position = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var packageOrder = package.Manifest?.AchievementOrder ?? new List<string>();
            for (var i = 0; i < packageOrder.Count; i++)
            {
                var apiName = Normalize(packageOrder[i]);
                if (apiName != null && !position.ContainsKey(apiName))
                {
                    position[apiName] = i;
                }
            }

            return order
                .Select((apiName, index) => new { Entry = entries[apiName], Index = index })
                .OrderBy(item => position.TryGetValue(item.Entry.ApiName, out var at) ? at : int.MaxValue)
                .ThenBy(item => item.Index)
                .Select(item => item.Entry)
                .Where(entry => entry.Changes != AchievementPreviewChange.None)
                .ToList();
        }

        private static void AddManifestEntries(GameCustomDataPortableFile manifest, Func<string, PackageEntry> entry)
        {
            if (manifest == null)
            {
                return;
            }

            AddCustomAchievementEntries(manifest.CustomAchievements, entry);

            foreach (var pair in manifest.AchievementOverrides ?? new Dictionary<string, AchievementOverride>())
            {
                var target = entry(pair.Key);
                var record = pair.Value;
                if (target == null || record == null)
                {
                    continue;
                }

                if (Normalize(record.DisplayName) != null)
                {
                    target.State.DisplayName = record.DisplayName;
                    target.Changes |= AchievementPreviewChange.Name;
                }

                if (Normalize(record.Description) != null)
                {
                    target.State.Description = record.Description;
                    target.Changes |= AchievementPreviewChange.Description;
                }

                if (Normalize(record.Category) != null)
                {
                    target.State.Category = record.Category;
                    target.Changes |= AchievementPreviewChange.Category;
                }

                if (record.ClearTags || Normalize(record.Tags) != null)
                {
                    target.State.Tags = AchievementTagHelper.ApplyOverride(
                        target.State.Tags,
                        record.ClearTags ? string.Empty : record.Tags);
                    target.Changes |= AchievementPreviewChange.Tags;
                }

                if (Normalize(record.Note) != null)
                {
                    target.State.Note = record.Note;
                    target.Changes |= AchievementPreviewChange.Note;
                }

                SetUnlockedIcon(target, record.UnlockedIconPath);
                SetLockedIcon(target, record.LockedIconPath);
            }

            foreach (var pair in manifest.AchievementUnlockedIconOverrides ?? new Dictionary<string, string>())
            {
                var target = entry(pair.Key);
                if (target != null && target.State.UnlockedIcon == null)
                {
                    SetUnlockedIcon(target, pair.Value);
                }
            }

            foreach (var pair in manifest.AchievementLockedIconOverrides ?? new Dictionary<string, string>())
            {
                var target = entry(pair.Key);
                if (target != null && target.State.LockedIcon == null)
                {
                    SetLockedIcon(target, pair.Value);
                }
            }

            foreach (var capstone in manifest.Capstones ?? new List<CapstoneAssignment>())
            {
                var target = entry(capstone?.ApiName);
                if (target != null)
                {
                    target.State.IsCapstone = true;
                    target.Changes |= AchievementPreviewChange.Capstone;
                }
            }

            foreach (var apiName in manifest.FilteredAchievementApiNames ?? new List<string>())
            {
                var target = entry(apiName);
                if (target != null)
                {
                    target.State.IsFiltered = true;
                    target.Changes |= AchievementPreviewChange.Filter;
                }
            }
        }

        private static void AddCustomAchievementEntries(
            IEnumerable<CustomAchievementDefinition> definitions,
            Func<string, PackageEntry> entry)
        {
            foreach (var definition in definitions ?? Enumerable.Empty<CustomAchievementDefinition>())
            {
                if (definition == null || Normalize(definition.DisplayName) == null)
                {
                    continue;
                }

                var target = entry(CustomAchievementProjectionService.BuildApiName(definition.Id));
                if (target == null)
                {
                    continue;
                }

                target.State.IsCustom = true;
                target.State.DisplayName = definition.DisplayName;
                target.State.Description = definition.Description;
                target.State.Category = Normalize(definition.Category);
                target.State.Tags = AchievementTagHelper.Normalize(definition.Tags);
                target.State.IsCapstone = definition.IsCapstone;
                target.Changes |= AchievementPreviewChange.Added;
                SetUnlockedIcon(target, definition.UnlockedIconPath);
                SetLockedIcon(target, definition.LockedIconPath);
            }
        }

        private static void SetUnlockedIcon(PackageEntry entry, string path)
        {
            path = Normalize(path);
            if (path != null)
            {
                entry.State.UnlockedIcon = path;
                entry.Changes |= AchievementPreviewChange.UnlockedIcon;
            }
        }

        private static void SetLockedIcon(PackageEntry entry, string path)
        {
            path = Normalize(path);
            if (path != null)
            {
                entry.State.LockedIcon = path;
                entry.Changes |= AchievementPreviewChange.LockedIcon;
            }
        }

        /// <summary>
        /// The package's entries plus the per-achievement keys of the schema-7 maps the entries
        /// do not read (category, category type, note, manual capstone).
        /// </summary>
        private static HashSet<string> BuildTouchedApiNames(
            GameCustomDataPortablePackage package,
            IEnumerable<PackageEntry> entries)
        {
            var touched = new HashSet<string>(entries.Select(entry => entry.ApiName), StringComparer.OrdinalIgnoreCase);
            var manifest = package.Shape == GameCustomDataPackageShape.Manifest ? package.Manifest : null;
            if (manifest == null)
            {
                return touched;
            }

            var keys = (manifest.AchievementCategoryOverrides?.Keys ?? Enumerable.Empty<string>())
                .Concat(manifest.AchievementCategoryTypeOverrides?.Keys ?? Enumerable.Empty<string>())
                .Concat(manifest.AchievementNotes?.Keys ?? Enumerable.Empty<string>())
                .Concat(new[] { manifest.ManualCapstoneApiName });
            foreach (var key in keys)
            {
                var apiName = Normalize(key);
                if (apiName != null)
                {
                    touched.Add(apiName);
                }
            }

            return touched;
        }

        private static void FillSummary(
            GameCustomDataPreviewDiff diff,
            GameCustomDataPortablePackage package,
            IReadOnlyList<PackageEntry> entries)
        {
            var manifest = package.Manifest;
            diff.IconCount = entries.Count(entry => entry.State.UnlockedIcon != null) +
                             entries.Count(entry => entry.State.LockedIcon != null);
            diff.NotesCount = entries.Count(entry => Normalize(entry.State.Note) != null);
            diff.CapstoneCount = entries.Count(entry => entry.State.IsCapstone);
            diff.OverrideCount = manifest?.AchievementOverrides?.Count(pair => pair.Value != null && !pair.Value.IsEmpty) ?? 0;
            diff.HasNotificationStyle = manifest?.NotificationAppearanceOverride != null;
            diff.CustomAchievementCount = entries.Count(entry => entry.State.IsCustom);

            var categories = new HashSet<string>(CategoryPathHelper.LabelComparer);
            foreach (var category in entries.Select(entry => entry.State.Category)
                         .Concat(manifest?.AchievementCategoryOrder ?? new List<string>()))
            {
                var normalized = Normalize(category);
                if (normalized != null)
                {
                    categories.Add(normalized);
                }
            }

            diff.CategoryCount = categories.Count;
        }

        private static string Normalize(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            return normalized.Length == 0 ? null : normalized;
        }
    }
}
