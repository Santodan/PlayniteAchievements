using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Images;
using PlayniteAchievements.Services.Metadata;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Services.Achievements
{
    /// <summary>
    /// Authors auto capstones, the one place both the editor's Auto Capstone button and automatic
    /// generation go through, so the two cannot drift apart in what they write.
    /// </summary>
    public sealed class AutoCapstoneAuthoring
    {
        private const string BaseId = "auto-capstone";

        // The art record's source once the user replaced an auto capstone's art.
        private const string UserArtSource = "user";

        private readonly GameCustomDataStore _store;
        private readonly AchievementOverridesService _overridesService;
        private readonly Func<Guid, GameAchievementData> _resolveGameData;
        private readonly Func<ManagedCustomIconService> _iconService;
        private readonly Func<AutoCapstoneTemplates> _templates;
        private readonly Func<MetadataLinkStore> _metadataLinks;
        private readonly ILogger _logger;

        /// <param name="templates">The text templates in effect, read at each authoring.</param>
        /// <param name="metadataLinks">Where the art written into each capstone is recorded.</param>
        public AutoCapstoneAuthoring(
            GameCustomDataStore store,
            AchievementOverridesService overridesService,
            Func<Guid, GameAchievementData> resolveGameData,
            Func<ManagedCustomIconService> iconService,
            Func<AutoCapstoneTemplates> templates,
            Func<MetadataLinkStore> metadataLinks,
            ILogger logger)
        {
            _store = store;
            _overridesService = overridesService;
            _resolveGameData = resolveGameData;
            _iconService = iconService;
            _templates = templates;
            _metadataLinks = metadataLinks;
            _logger = logger;
        }

        /// <summary>
        /// Brings each refreshed game's auto capstones in line with what they should now use (a
        /// newly matched platinum, a PS5 set replacing a PS4 one, or a changed Auto capstone
        /// choice): the art, and on the whole-game capstone the platinum's title and description.
        /// What the user replaced is left alone: an icon file that no longer matches what the
        /// plugin recorded writing, or text that is neither a template's nor the recorded platinum's.
        /// </summary>
        public async Task RefreshArtAsync(IEnumerable<Guid> gameIds)
        {
            foreach (var gameId in (gameIds ?? Enumerable.Empty<Guid>()).Distinct())
            {
                try
                {
                    await RefreshArtAsync(gameId).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger?.Warn(ex, $"Failed refreshing auto capstone art for gameId={gameId}.");
                }
            }
        }

        private async Task RefreshArtAsync(Guid gameId)
        {
            var links = _metadataLinks?.Invoke();
            if (gameId == Guid.Empty || links == null || _store == null || _overridesService == null)
            {
                return;
            }

            // An open editor saves its own rows over this write, so the game waits for it to close.
            if (OpenEditorRegistry.DeferWhileOpen(gameId, this, () => _ = Task.Run(() => RefreshArtAsync(new[] { gameId }))))
            {
                _logger?.Debug($"[AutoCapstone] Art for gameId={gameId} waits for its editor to close.");
                return;
            }

            var definitions = _store.LoadOrDefault(gameId)?.CustomAchievements;
            if (definitions == null || !definitions.Any(definition => definition?.IsAutoCapstone == true))
            {
                return;
            }

            var game = API.Instance?.Database?.Games?.Get(gameId);
            var gameSource = AutoCapstoneTemplate.ResolveIconSource(game, _logger);
            var wholeBorrowedArt = AutoCapstoneTemplate.MetadataArtAccessor?.Invoke(gameId);
            var wholeBorrowedText = BorrowedText(gameId);
            var gameName = ResolveGameName(game);
            var categoryCapstones = links.GetCategoryCapstones(gameId);
            var categoryOf = categoryCapstones.Count == 0
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : LoadAchievementsInOrder(gameId)
                    .Where(achievement => achievement?.ApiName != null)
                    .GroupBy(achievement => achievement.ApiName, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First().Category, StringComparer.OrdinalIgnoreCase);
            var existing = definitions.Where(definition => definition != null).ToList();
            var replacement = existing.Select(definition => definition.Clone()).ToList();
            var changed = false;
            foreach (var definition in replacement.Where(definition => definition.IsAutoCapstone))
            {
                var apiName = CustomAchievementProjectionService.BuildApiName(definition.Id);
                var record = links.GetCapstoneArtRecord(gameId, apiName);

                // A category capstone of a collection takes its own game's platinum; the whole
                // game's takes the game's platinum.
                var category = definition.IsWholeGameAutoCapstone ? null : (categoryOf.TryGetValue(apiName, out var filed) ? filed : null);
                var own = CategoryCapstone(categoryCapstones, category);
                var borrowedArt = own != null ? own.ArtUrl : definition.IsWholeGameAutoCapstone ? wholeBorrowedArt : null;
                var source = own?.ArtUrl ?? gameSource;
                var borrowedText = own != null ? BorrowedText(own) : definition.IsWholeGameAutoCapstone ? wholeBorrowedText : (null, null);
                var templateText = AutoCapstoneText.Describe(_templates?.Invoke(), gameName, definition.IsWholeGameAutoCapstone ? null : category);

                var artWritten = false;
                if (!string.IsNullOrWhiteSpace(source) && ShouldReplaceArt(links, gameId, apiName, definition, record, source, borrowedArt))
                {
                    var path = await MaterializeIconAsync(gameId, source, existing, apiName).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        DeleteReplacedCopy(gameId, definition.UnlockedIconPath, path);
                        definition.UnlockedIconPath = path;
                        artWritten = true;
                        _logger?.Debug($"[AutoCapstone] Art for gameId={gameId} {apiName} now from {source}.");
                    }
                }

                // A capstone stands for what a platinum does when it has one: the whole game's,
                // or its own game's in a collection.
                var takesText = definition.IsWholeGameAutoCapstone || own != null;
                var textWritten = takesText && ApplyText(definition, record, borrowedText, templateText);
                if (textWritten)
                {
                    _logger?.Debug($"[AutoCapstone] Text for gameId={gameId} {apiName} now from {(borrowedText.Title != null ? "the platinum" : "the templates")}.");
                }

                if (artWritten || textWritten)
                {
                    // An unrecorded source keeps the rule for capstones authored before records.
                    RecordArt(
                        links,
                        gameId,
                        apiName,
                        artWritten ? source : record?.Source,
                        definition.UnlockedIconPath,
                        takesText ? borrowedText : (null, null));

                    // Counted even when the icon path is unchanged: new art overwrites the managed
                    // file in place, and only the write's change event makes open views read it again.
                    changed = true;
                }
            }

            if (changed)
            {
                _overridesService.SetCustomAchievements(gameId, replacement);
            }
        }

        /// <summary>
        /// Whether the capstone's art should become <paramref name="source"/>. An icon the user
        /// replaced since the plugin wrote it is theirs from then on.
        /// </summary>
        private static bool ShouldReplaceArt(
            MetadataLinkStore links,
            Guid gameId,
            string apiName,
            CustomAchievementDefinition definition,
            CapstoneArtRecord record,
            string source,
            string borrowedArt)
        {
            if (record?.Source != null)
            {
                if (string.Equals(record.Source, source, StringComparison.Ordinal) ||
                    string.Equals(record.Source, UserArtSource, StringComparison.Ordinal))
                {
                    return false;
                }

                if (!IsRecordedFile(gameId, definition.UnlockedIconPath, record))
                {
                    // The user's art from now on, kept as a record so it is told apart from art
                    // authored before records were kept.
                    links.SetCapstoneArtRecord(gameId, apiName, new CapstoneArtRecord { Source = UserArtSource, Title = record.Title, Description = record.Description });
                    return false;
                }

                return true;
            }

            // Authored before art was recorded: only a borrowed platinum, which the platform's
            // Auto capstone choice asked for, replaces what it has.
            return string.Equals(source, borrowedArt, StringComparison.Ordinal);
        }

        /// <summary>
        /// Brings a whole-game capstone's title and description to the platinum's text, or back to
        /// the templates' when no platinum supplies any. Only text the plugin wrote is replaced:
        /// the templates' current text, or the platinum text it recorded writing. Returns whether
        /// anything changed.
        /// </summary>
        private static bool ApplyText(
            CustomAchievementDefinition definition,
            CapstoneArtRecord record,
            (string Title, string Description) borrowed,
            (string Title, string Description) template)
        {
            var desired = borrowed.Title != null ? borrowed : template;
            var changed = false;
            if (!string.Equals(definition.DisplayName, desired.Title, StringComparison.Ordinal) &&
                IsPluginText(definition.DisplayName, record?.Title, template.Title))
            {
                definition.DisplayName = desired.Title;
                changed = true;
            }

            if (!string.Equals(definition.Description ?? string.Empty, desired.Description ?? string.Empty, StringComparison.Ordinal) &&
                IsPluginText(definition.Description, record?.Description, template.Description))
            {
                definition.Description = desired.Description;
                changed = true;
            }

            return changed;
        }

        private static bool IsPluginText(string current, string recorded, string template)
        {
            current = current ?? string.Empty;
            return (recorded != null && string.Equals(current, recorded, StringComparison.Ordinal)) ||
                   string.Equals(current, template ?? string.Empty, StringComparison.Ordinal);
        }

        private static string ResolveGameName(Game game)
        {
            var name = (game?.Name ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(name) ? ResourceProvider.GetString(AutoCapstoneText.FallbackGameNameKey) : name;
        }

        private static bool IsRecordedFile(Guid gameId, string path, CapstoneArtRecord record)
        {
            if (string.IsNullOrWhiteSpace(path) ||
                !string.Equals(path, record.Path, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // A borrowed platinum's file belongs to its source, which rewrites or removes it; only
            // the user's own pick moves the capstone off that path.
            if (AchievementIconCachePathBuilder.IsMetadataSourceIconPath(path, gameId.ToString("D")))
            {
                return true;
            }

            if (!File.Exists(path))
            {
                return false;
            }

            var info = new FileInfo(path);
            return info.Length == record.Length &&
                   Math.Abs((info.LastWriteTimeUtc - record.WriteTimeUtc).TotalSeconds) < 2;
        }

        /// <summary>
        /// Deletes the capstone's own copy of its art once the art moves to another file, such as
        /// the platinum's file in a metadata source folder. Called only for art the plugin wrote.
        /// </summary>
        private void DeleteReplacedCopy(Guid gameId, string previousPath, string newPath)
        {
            if (string.IsNullOrWhiteSpace(previousPath) ||
                string.Equals(previousPath, newPath, StringComparison.OrdinalIgnoreCase) ||
                _iconService?.Invoke()?.IsManagedCustomIconPath(previousPath, gameId.ToString("D")) != true)
            {
                return;
            }

            try
            {
                if (File.Exists(previousPath))
                {
                    File.Delete(previousPath);
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"[AutoCapstone] Failed deleting the replaced art '{previousPath}' of gameId={gameId}.");
            }
        }

        /// <param name="borrowedText">The borrowed text written with it, or nulls for the templates'.</param>
        private static void RecordArt(
            MetadataLinkStore links,
            Guid gameId,
            string apiName,
            string source,
            string path,
            (string Title, string Description) borrowedText)
        {
            if (links == null || !File.Exists(path))
            {
                return;
            }

            var info = new FileInfo(path);
            links.SetCapstoneArtRecord(gameId, apiName, new CapstoneArtRecord
            {
                Source = source,
                Path = path,
                Length = info.Length,
                WriteTimeUtc = info.LastWriteTimeUtc,
                Title = borrowedText.Title,
                Description = borrowedText.Title == null ? null : borrowedText.Description
            });
        }

        /// <summary>The platinum text a metadata source supplied for the game, or nulls.</summary>
        private static (string Title, string Description) BorrowedText(Guid gameId)
        {
            var borrowed = AutoCapstoneTemplate.MetadataTextAccessor?.Invoke(gameId) ?? (null, null);
            return string.IsNullOrWhiteSpace(borrowed.Title) ? (null, null) : (borrowed.Title, borrowed.Description ?? string.Empty);
        }

        /// <summary>A collection game's platinum text, or nulls.</summary>
        private static (string Title, string Description) BorrowedText(MetadataCategoryCapstone capstone) =>
            string.IsNullOrWhiteSpace(capstone?.Title) ? (null, null) : (capstone.Title, capstone.Description ?? string.Empty);

        /// <summary>The borrowed platinum of a collection's game filed under <paramref name="category"/>, or null.</summary>
        private static MetadataCategoryCapstone CategoryCapstone(IReadOnlyList<MetadataCategoryCapstone> capstones, string category) =>
            string.IsNullOrWhiteSpace(category)
                ? null
                : capstones?.FirstOrDefault(capstone => string.Equals(capstone?.Category?.Trim(), category.Trim(), CategoryPathHelper.LabelComparison));

        /// <summary>Each collection game's borrowed platinum by category, as the last overlay recorded them; empty when none.</summary>
        public IReadOnlyList<MetadataCategoryCapstone> CategoryCapstones(Guid gameId) =>
            gameId == Guid.Empty
                ? Array.Empty<MetadataCategoryCapstone>()
                : _metadataLinks?.Invoke()?.GetCategoryCapstones(gameId) ?? Array.Empty<MetadataCategoryCapstone>();

        /// <summary>
        /// The game's hydrated achievements, authored ones included, in the order the game shows
        /// them, which is the order a platinum is picked in.
        /// </summary>
        public IReadOnlyList<AchievementDetail> LoadAchievementsInOrder(Guid gameId)
        {
            var gameData = _resolveGameData?.Invoke(gameId);
            return AchievementOrderHelper.ApplyOrder(
                gameData?.Achievements ?? new List<AchievementDetail>(),
                achievement => achievement?.ApiName,
                gameData?.AchievementOrder);
        }

        /// <summary>
        /// The game's platinum to nominate for the whole game, else the achievement a metadata
        /// source's platinum matched, as automatic generation picks them; null when it has neither.
        /// </summary>
        public AchievementDetail SelectPlatinum(Guid gameId, IReadOnlyList<AchievementDetail> achievementsInOrder)
        {
            return AutoCapstoneEligibility.SelectNominee(achievementsInOrder, MatchedCapstoneApiName(gameId));
        }

        /// <summary>
        /// Nominates a platinum as the game's capstone and marks the game as handled.
        /// </summary>
        public bool NominatePlatinum(Guid gameId, AchievementDetail platinum)
        {
            if (gameId == Guid.Empty || string.IsNullOrWhiteSpace(platinum?.ApiName))
            {
                return false;
            }

            var nominated = _overridesService.SetCapstone(gameId, platinum.ApiName, true);
            if (nominated?.Success != true)
            {
                return false;
            }

            LabelPlatinum(gameId, platinum);
            _overridesService.MarkAutoCapstoneGenerated(gameId);
            return true;
        }

        /// <summary>
        /// Gives a nominated achievement with no trophy type the platinum's, as an authored auto
        /// capstone has, in the game's custom data. A type the achievement has, or a None the user
        /// chose, is kept.
        /// </summary>
        private void LabelPlatinum(Guid gameId, AchievementDetail achievement)
        {
            if (achievement == null || !string.IsNullOrWhiteSpace(achievement.TrophyType) || achievement.IsCustom)
            {
                return;
            }

            var overrides = _store?.LoadOrDefault(gameId)?.AchievementOverrides;
            if (overrides != null &&
                overrides.TryGetValue(achievement.ApiName, out var existing) &&
                existing?.ClearTrophyType == true)
            {
                return;
            }

            _overridesService.SetAchievementFieldOverride(
                gameId,
                achievement.ApiName,
                AchievementEditableField.TrophyType,
                AutoCapstoneTemplate.PlatinumTrophyType);
        }

        /// <summary>
        /// ApiName of the game's own achievement a metadata source's platinum matched, its real
        /// capstone, or null when none did.
        /// </summary>
        public string MatchedCapstoneApiName(Guid gameId)
        {
            return gameId == Guid.Empty ? null : _metadataLinks?.Invoke()?.GetCapstoneMatch(gameId);
        }

        /// <summary>
        /// Puts the game's own achievement a metadata source's platinum matched in place of its
        /// whole-game auto capstone, while that capstone is still only what the plugin wrote: still
        /// a capstone, with the art and text the plugin recorded. Returns whether it was replaced.
        /// </summary>
        public bool ReplaceWithMatchedCapstone(Guid gameId)
        {
            var links = _metadataLinks?.Invoke();
            var matchApiName = MatchedCapstoneApiName(gameId);
            if (string.IsNullOrWhiteSpace(matchApiName) || links == null || _store == null || _overridesService == null)
            {
                return false;
            }

            var definitions = _store.LoadOrDefault(gameId)?.CustomAchievements?
                .Where(definition => definition != null)
                .ToList();
            var authored = definitions?.FirstOrDefault(definition =>
                definition.IsAutoCapstone && definition.IsWholeGameAutoCapstone);
            if (authored == null)
            {
                return false;
            }

            var authoredApiName = CustomAchievementProjectionService.BuildApiName(authored.Id);
            var achievements = LoadAchievementsInOrder(gameId);
            var match = achievements.FirstOrDefault(achievement =>
                achievement != null &&
                !achievement.IsCustom &&
                string.Equals(achievement.ApiName, matchApiName, StringComparison.OrdinalIgnoreCase));
            var current = achievements.FirstOrDefault(achievement =>
                string.Equals(achievement?.ApiName, authoredApiName, StringComparison.OrdinalIgnoreCase));
            if (match == null || current?.IsCapstone != true)
            {
                _logger?.Debug(
                    $"[AutoCapstone] gameId={gameId} keeps {authoredApiName}: " +
                    (match == null ? $"matched platinum {matchApiName} is not among its achievements." : "it is no longer a capstone."));
                return false;
            }

            var templateText = AutoCapstoneText.Describe(
                _templates?.Invoke(),
                ResolveGameName(API.Instance?.Database?.Games?.Get(gameId)),
                null);
            var record = links.GetCapstoneArtRecord(gameId, authoredApiName);
            if (!IsUntouched(gameId, authored, record, templateText))
            {
                _logger?.Debug(
                    $"[AutoCapstone] gameId={gameId} keeps {authoredApiName} over matched platinum {matchApiName}: edited " +
                    $"(record={(record == null ? "none" : "yes")}, icon='{authored.UnlockedIconPath}', recordedIcon='{record?.Path}', " +
                    $"lockedIcon={!string.IsNullOrWhiteSpace(authored.LockedIconPath)}, title='{authored.DisplayName}', " +
                    $"recordedTitle='{record?.Title}', templateTitle='{templateText.Title}').");
                return false;
            }

            // Nominated first, so a nomination that fails leaves the authored capstone in place.
            // Nominating displaces it within its category; removing it then drops it from the
            // capstone list wherever it was filed.
            var nominated = _overridesService.SetCapstone(gameId, match.ApiName, true);
            if (nominated?.Success != true)
            {
                _logger?.Warn($"[AutoCapstone] Could not nominate {match.ApiName} for gameId={gameId}; its auto capstone stays.");
                return false;
            }

            LabelPlatinum(gameId, match);

            _overridesService.SetCustomAchievements(
                gameId,
                definitions.Where(definition => !ReferenceEquals(definition, authored)).ToList());
            links.SetCapstoneArtRecord(gameId, authoredApiName, null);

            _logger?.Debug($"[AutoCapstone] gameId={gameId} {authoredApiName} replaced by its matched platinum {match.ApiName}.");
            return true;
        }

        /// <summary>
        /// For a collection: each of its games' categories without a capstone gets one, the
        /// achievement its game's platinum matched, else an authored capstone with that platinum's
        /// art and text. Marks the game as handled. Returns how many it added.
        /// </summary>
        public async Task<int> GenerateCategoryCapstonesAsync(Guid gameId, IReadOnlyList<MetadataCategoryCapstone> capstones)
        {
            if (gameId == Guid.Empty || _overridesService == null || capstones == null || capstones.Count == 0)
            {
                return 0;
            }

            var pending = capstones
                .Where(capstone => !string.IsNullOrWhiteSpace(capstone?.Category) && !IsCategoryHandled(gameId, capstone.Category.Trim()))
                .ToList();
            if (pending.Count == 0)
            {
                return 0;
            }

            var achievements = LoadAchievementsInOrder(gameId);
            var covered = new HashSet<string>(
                achievements
                    .Where(achievement => achievement?.IsCapstone == true && !string.IsNullOrWhiteSpace(achievement.Category))
                    .Select(achievement => achievement.Category.Trim()),
                CategoryPathHelper.LabelComparer);
            var added = 0;
            foreach (var capstone in pending)
            {
                var category = capstone.Category.Trim();

                // Handled once: a category's capstone the user removes is not put back.
                MarkCategoryHandled(gameId, category);
                if (!covered.Add(category))
                {
                    continue;
                }

                var match = string.IsNullOrWhiteSpace(capstone.MatchApiName)
                    ? null
                    : achievements.FirstOrDefault(achievement =>
                        achievement != null &&
                        !achievement.IsCustom &&
                        string.Equals(achievement.ApiName, capstone.MatchApiName, StringComparison.OrdinalIgnoreCase));
                if (match != null && _overridesService.SetCapstone(gameId, match.ApiName, true)?.Success == true)
                {
                    LabelPlatinum(gameId, match);
                    added++;
                    continue;
                }

                if (await AuthorAsync(gameId, category).ConfigureAwait(false) != null)
                {
                    added++;
                }
            }

            _overridesService.MarkAutoCapstoneGenerated(gameId);
            _logger?.Debug($"[AutoCapstone] gameId={gameId} got {added} capstone(s) for its collection's games.");
            return added;
        }

        // A handled collection category is kept beside the capstone art records, under a key no
        // achievement ApiName takes, so it survives a cache clear and is never exported.
        private const string HandledCategoryKeyPrefix = "auto-capstone-category:";

        /// <summary>Whether the game's collection category already got its capstone once.</summary>
        public bool IsCategoryHandled(Guid gameId, string category) =>
            _metadataLinks?.Invoke()?.GetCapstoneArtRecord(gameId, HandledCategoryKeyPrefix + category) != null;

        private void MarkCategoryHandled(Guid gameId, string category) =>
            _metadataLinks?.Invoke()?.SetCapstoneArtRecord(gameId, HandledCategoryKeyPrefix + category, new CapstoneArtRecord { Title = category });

        /// <summary>
        /// Removes the game's whole-game auto capstone while it still holds only what the plugin
        /// wrote, as when the game turned out to be a collection whose games each get their own.
        /// Returns whether it was removed.
        /// </summary>
        public bool RemoveUntouchedWholeGameCapstone(Guid gameId)
        {
            var links = _metadataLinks?.Invoke();
            if (gameId == Guid.Empty || links == null || _store == null || _overridesService == null)
            {
                return false;
            }

            var definitions = _store.LoadOrDefault(gameId)?.CustomAchievements?
                .Where(definition => definition != null)
                .ToList();
            var authored = definitions?.FirstOrDefault(definition => definition.IsAutoCapstone && definition.IsWholeGameAutoCapstone);
            if (authored == null)
            {
                return false;
            }

            var apiName = CustomAchievementProjectionService.BuildApiName(authored.Id);
            var templateText = AutoCapstoneText.Describe(
                _templates?.Invoke(),
                ResolveGameName(API.Instance?.Database?.Games?.Get(gameId)),
                null);
            var record = links.GetCapstoneArtRecord(gameId, apiName);
            if (!IsUntouched(gameId, authored, record, templateText))
            {
                _logger?.Debug(
                    $"[AutoCapstone] gameId={gameId} keeps its edited whole-game capstone {apiName} " +
                    $"(record={(record == null ? "none" : "yes")}, icon='{authored.UnlockedIconPath}', recordedIcon='{record?.Path}', " +
                    $"recordedFile={(record != null && !string.IsNullOrWhiteSpace(authored.UnlockedIconPath) && IsRecordedFile(gameId, authored.UnlockedIconPath, record))}, " +
                    $"lockedIcon={!string.IsNullOrWhiteSpace(authored.LockedIconPath)}, title='{authored.DisplayName}', " +
                    $"recordedTitle='{record?.Title}', templateTitle='{templateText.Title}', description='{authored.Description}', " +
                    $"recordedDescription='{record?.Description}', templateDescription='{templateText.Description}').");
                return false;
            }

            _overridesService.SetCustomAchievements(gameId, definitions.Where(definition => !ReferenceEquals(definition, authored)).ToList());
            links.SetCapstoneArtRecord(gameId, apiName, null);
            _logger?.Debug($"[AutoCapstone] gameId={gameId} {apiName} removed: the game is a collection whose games get their own.");
            return true;
        }

        /// <summary>
        /// Whether a whole-game auto capstone holds only what the plugin wrote: its recorded art
        /// (or none) and the templates' or the recorded platinum's text.
        /// </summary>
        private static bool IsUntouched(
            Guid gameId,
            CustomAchievementDefinition definition,
            CapstoneArtRecord record,
            (string Title, string Description) template)
        {
            if (definition == null || !string.IsNullOrWhiteSpace(definition.LockedIconPath))
            {
                return false;
            }

            // Without a record (authored before art was recorded) the art cannot be checked, and
            // the text decides; art the user replaced is recorded as theirs.
            var pluginArt = string.IsNullOrWhiteSpace(definition.UnlockedIconPath) ||
                            record == null ||
                            (!string.Equals(record.Source, UserArtSource, StringComparison.Ordinal) && IsRecordedFile(gameId, definition.UnlockedIconPath, record));
            return pluginArt &&
                   IsPluginText(definition.DisplayName, record?.Title, template.Title) &&
                   IsPluginText(definition.Description, record?.Description, template.Description);
        }

        /// <summary>
        /// Authors an auto capstone, files it, nominates it, and marks the game as handled.
        /// </summary>
        /// <param name="category">The category it stands for, or null for the whole game.</param>
        /// <returns>The new capstone's ApiName, or null when there was nothing for it to stand for.</returns>
        public async Task<string> AuthorAsync(Guid gameId, string category = null)
        {
            if (gameId == Guid.Empty || _store == null || _overridesService == null)
            {
                return null;
            }

            var stored = _store.TryLoad(gameId, out var data) ? data : null;
            var gameData = _resolveGameData?.Invoke(gameId);
            var achievements = AchievementOrderHelper.ApplyOrder(
                gameData?.Achievements ?? new List<AchievementDetail>(),
                achievement => achievement?.ApiName,
                gameData?.AchievementOrder);

            // Worked out now, so a game already finished gets a capstone already unlocked and the
            // first refresh after this sees no crossing to announce.
            var normalizedCategory = AchievementCategoryHelper.NormalizeCategory(category);
            var derived = AutoCapstoneCalculator.Derive(
                achievements,
                normalizedCategory,
                gameData?.AchievementCategoryOrder);
            if (derived == null)
            {
                return null;
            }

            var game = API.Instance?.Database?.Games?.Get(gameId);
            var name = (game?.Name ?? gameData?.GameName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                name = ResourceProvider.GetString(AutoCapstoneText.FallbackGameNameKey);
            }

            var existing = stored?.CustomAchievements?
                .Where(definition => definition != null)
                .ToList() ?? new List<CustomAchievementDefinition>();
            var id = ResolveUniqueId(existing);
            var apiName = CustomAchievementProjectionService.BuildApiName(id);
            // A collection's game takes its own platinum's art and text, as the whole game takes its.
            var own = CategoryCapstone(CategoryCapstones(gameId), normalizedCategory);
            var borrowedText = normalizedCategory == null ? BorrowedText(gameId) : BorrowedText(own);
            var (title, description) = normalizedCategory == null
                ? AutoCapstoneTemplate.ResolveWholeGameText(gameId, _templates?.Invoke(), name)
                : borrowedText.Title != null
                    ? borrowedText
                    : AutoCapstoneText.Describe(_templates?.Invoke(), name, normalizedCategory);
            var iconSource = own?.ArtUrl ?? AutoCapstoneTemplate.ResolveIconSource(game, _logger);
            var iconPath = await MaterializeIconAsync(gameId, iconSource, existing, apiName).ConfigureAwait(false);

            var definition = new CustomAchievementDefinition
            {
                Id = id,
                DisplayName = title,
                Description = description,
                Unlocked = derived.Unlocked,
                UnlockTimeUtc = derived.Unlocked ? derived.UnlockTimeUtc : null,
                UnlockedIconPath = iconPath,
                CategoryKind = derived.CategoryKind,
                TrophyType = AutoCapstoneTemplate.PlatinumTrophyType,
                Hidden = false,
                Rarity = derived.Rarity ?? "Common",
                GlobalPercentUnlocked = derived.GlobalPercentUnlocked,
                IsAutoCapstone = true,
                IsWholeGameAutoCapstone = normalizedCategory == null
            };

            // A chosen category is filed outright; the whole game is filed with the main game.
            _overridesService.AddGeneratedAutoCapstone(gameId, definition, normalizedCategory ?? derived.Category);
            RecordArt(_metadataLinks?.Invoke(), gameId, apiName, iconSource, iconPath, borrowedText);

            var nominated = _overridesService.SetCapstone(gameId, apiName, true);
            if (nominated?.Success != true)
            {
                _logger?.Warn($"Authored an auto capstone for gameId={gameId} but could not nominate it.");
            }

            return apiName;
        }

        /// <summary>
        /// A stable id, so the capstone's ApiName reads the same in every game, suffixed only when
        /// the game already authored something with it.
        /// </summary>
        private static string ResolveUniqueId(IReadOnlyList<CustomAchievementDefinition> existing)
        {
            var used = new HashSet<string>(
                existing.Select(definition => CustomAchievementProjectionService.NormalizeId(definition.Id))
                    .Where(value => !string.IsNullOrWhiteSpace(value)),
                StringComparer.OrdinalIgnoreCase);

            var candidate = BaseId;
            for (var suffix = 2; used.Contains(candidate); suffix++)
            {
                candidate = BaseId + "-" + suffix;
            }

            return candidate;
        }

        /// <summary>
        /// Copies the capstone's art into the game's icon cache, the way the editor's save does for
        /// an icon dropped onto an authored achievement. The source path stands in when the copy
        /// cannot be made, which the next editor save materializes in turn.
        /// </summary>
        private async Task<string> MaterializeIconAsync(
            Guid gameId,
            string source,
            IReadOnlyList<CustomAchievementDefinition> existing,
            string apiName)
        {
            var iconService = _iconService?.Invoke();
            if (string.IsNullOrWhiteSpace(source) || iconService == null)
            {
                return source;
            }

            // A platinum the game already saved from its metadata source is shown from that file.
            if (AchievementIconCachePathBuilder.IsMetadataSourceIconPath(source, gameId.ToString("D")) && File.Exists(source))
            {
                return source;
            }

            // Stems for every authored achievement, not only this one, because a collision between
            // two of them is what decides the suffix each gets. Distinct, since a capstone whose art
            // is being refreshed is already among them.
            var stems = AchievementIconCachePathBuilder.BuildFileStems(
                existing.Select(definition => CustomAchievementProjectionService.BuildApiName(definition.Id))
                    .Concat(new[] { apiName })
                    .Distinct(StringComparer.OrdinalIgnoreCase));
            if (!stems.TryGetValue(apiName, out var stem) || string.IsNullOrWhiteSpace(stem))
            {
                return source;
            }

            try
            {
                var managed = await iconService
                    .MaterializeCustomIconAsync(
                        source,
                        gameId.ToString("D"),
                        stem,
                        AchievementIconVariant.Unlocked,
                        CancellationToken.None,
                        overwriteExistingTarget: true)
                    .ConfigureAwait(false);
                return string.IsNullOrWhiteSpace(managed) ? source : managed;
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"Failed caching the auto capstone icon for gameId={gameId}.");
                return source;
            }
        }
    }
}
