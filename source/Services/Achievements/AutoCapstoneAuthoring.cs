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
            var source = AutoCapstoneTemplate.ResolveIconSource(game, _logger);
            var borrowedArt = AutoCapstoneTemplate.MetadataArtAccessor?.Invoke(gameId);
            var borrowedText = BorrowedText(gameId);
            var templateText = AutoCapstoneText.Describe(_templates?.Invoke(), ResolveGameName(game), null);
            var existing = definitions.Where(definition => definition != null).ToList();
            var replacement = existing.Select(definition => definition.Clone()).ToList();
            var changed = false;
            foreach (var definition in replacement.Where(definition => definition.IsAutoCapstone))
            {
                var apiName = CustomAchievementProjectionService.BuildApiName(definition.Id);
                var record = links.GetCapstoneArtRecord(gameId, apiName);

                var artWritten = false;
                if (!string.IsNullOrWhiteSpace(source) && ShouldReplaceArt(links, gameId, apiName, definition, record, source, borrowedArt))
                {
                    var path = await MaterializeIconAsync(gameId, source, existing, apiName).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        definition.UnlockedIconPath = path;
                        artWritten = true;
                        _logger?.Debug($"[AutoCapstone] Art for gameId={gameId} {apiName} now from {source}.");
                    }
                }

                // Only the whole-game capstone stands for what a platinum does.
                var textWritten = definition.IsWholeGameAutoCapstone &&
                                  ApplyText(definition, record, borrowedText, templateText);
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
                        definition.IsWholeGameAutoCapstone ? borrowedText : (null, null));

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
                if (string.Equals(record.Source, source, StringComparison.Ordinal))
                {
                    return false;
                }

                if (!IsRecordedFile(definition.UnlockedIconPath, record))
                {
                    links.SetCapstoneArtRecord(gameId, apiName, null);
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

        private static bool IsRecordedFile(string path, CapstoneArtRecord record)
        {
            if (string.IsNullOrWhiteSpace(path) ||
                !string.Equals(path, record.Path, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(path))
            {
                return false;
            }

            var info = new FileInfo(path);
            return info.Length == record.Length &&
                   Math.Abs((info.LastWriteTimeUtc - record.WriteTimeUtc).TotalSeconds) < 2;
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

        /// <summary>The game's platinum to nominate for the whole game, or null when it has none.</summary>
        public static AchievementDetail SelectPlatinum(IEnumerable<AchievementDetail> achievementsInOrder)
        {
            return AutoCapstoneTemplate.SelectPlatinum(
                achievementsInOrder,
                achievement => achievement.TrophyType,
                achievement => achievement.CategoryType);
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

            _overridesService.MarkAutoCapstoneGenerated(gameId);
            return true;
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
            var achievements = gameData?.Achievements ?? new List<AchievementDetail>();

            // Worked out now, so a game already finished gets a capstone already unlocked and the
            // first refresh after this sees no crossing to announce.
            var normalizedCategory = AchievementCategoryTypeHelper.NormalizeCategory(category);
            var derived = AutoCapstoneCalculator.Derive(achievements, normalizedCategory);
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
            var borrowedText = normalizedCategory == null ? BorrowedText(gameId) : (null, null);
            var (title, description) = normalizedCategory == null
                ? AutoCapstoneTemplate.ResolveWholeGameText(gameId, _templates?.Invoke(), name)
                : AutoCapstoneText.Describe(_templates?.Invoke(), name, normalizedCategory);
            var iconSource = AutoCapstoneTemplate.ResolveIconSource(game, _logger);
            var iconPath = await MaterializeIconAsync(gameId, iconSource, existing, apiName).ConfigureAwait(false);

            var definition = new CustomAchievementDefinition
            {
                Id = id,
                DisplayName = title,
                Description = description,
                Unlocked = derived.Unlocked,
                UnlockTimeUtc = derived.Unlocked ? derived.UnlockTimeUtc : null,
                UnlockedIconPath = iconPath,
                CategoryType = derived.CategoryType,
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
