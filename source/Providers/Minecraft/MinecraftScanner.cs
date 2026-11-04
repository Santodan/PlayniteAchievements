using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Refresh;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Minecraft
{
    /// <summary>
    /// Refreshes Java Minecraft advancements: definitions from the game jar, mods and world
    /// datapacks; progress from each world's advancement file for the player, merged so an
    /// advancement is unlocked once any world completes it.
    /// </summary>
    internal sealed class MinecraftScanner
    {
        private const string MinecraftNamespace = "minecraft";

        // In-game tab order of the vanilla roots; other tabs follow by title.
        private static readonly string[] VanillaRootOrder =
        {
            "minecraft:story/root",
            "minecraft:nether/root",
            "minecraft:end/root",
            "minecraft:adventure/root",
            "minecraft:husbandry/root"
        };

        private readonly ILogger _logger;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly MinecraftSettings _providerSettings;
        private readonly IPlayniteAPI _playniteApi;
        private readonly string _pluginUserDataPath;

        public MinecraftScanner(
            ILogger logger,
            PlayniteAchievementsSettings settings,
            MinecraftSettings providerSettings,
            IPlayniteAPI playniteApi,
            string pluginUserDataPath)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _providerSettings = providerSettings ?? throw new ArgumentNullException(nameof(providerSettings));
            _playniteApi = playniteApi;
            _pluginUserDataPath = pluginUserDataPath ?? string.Empty;
        }

        public async Task<RebuildPayload> RefreshAsync(
            IReadOnlyList<Game> gamesToRefresh,
            Action<Game> onGameStarting,
            Func<Game, GameAchievementData, Task> onGameCompleted,
            CancellationToken cancel)
        {
            if (gamesToRefresh == null || gamesToRefresh.Count == 0)
            {
                return new RebuildPayload { Summary = new RebuildSummary() };
            }

            var iconExtractor = new MinecraftIconExtractor(_pluginUserDataPath, _logger);
            return await ProviderRefreshExecutor.RunProviderGamesAsync(
                gamesToRefresh,
                onGameStarting,
                (game, token) =>
                {
                    var source = MinecraftGameResolver.Resolve(game, _providerSettings, _playniteApi);
                    if (source == null)
                    {
                        _logger?.Info($"[Minecraft] '{game?.Name}': no Minecraft folder resolved; cached achievements preserved.");
                        return Task.FromResult(new ProviderRefreshExecutor.ProviderGameResult { Data = null });
                    }

                    var data = BuildGameData(game, source, iconExtractor, token);
                    return Task.FromResult(new ProviderRefreshExecutor.ProviderGameResult { Data = data });
                },
                onGameCompleted,
                isAuthRequiredException: _ => false,
                onGameError: (game, ex, consecutiveErrors) =>
                    _logger?.Error(ex, $"[Minecraft] Failed to scan '{game?.Name}'"),
                delayBetweenGamesAsync: null,
                delayAfterErrorAsync: null,
                cancel).ConfigureAwait(false) ?? new RebuildPayload { Summary = new RebuildSummary() };
        }

        /// <summary>
        /// The game's advancements, one set per instance, or null when no instance yields
        /// definitions or a player's progress could not be read, so the cached achievements are kept.
        /// </summary>
        internal GameAchievementData BuildGameData(
            Game game,
            MinecraftGameSource source,
            MinecraftIconExtractor iconExtractor,
            CancellationToken cancel)
        {
            if (source.Instances.Count == 0)
            {
                _logger?.Info(
                    $"[Minecraft] '{game.Name}': no instance with advancement data (resolved by {source.ResolvedBy}; skipped " +
                    $"{(source.SkippedFolders.Count == 0 ? "nothing" : string.Join(", ", source.SkippedFolders))}); cached achievements preserved.");
                return null;
            }

            var locale = MinecraftPaths.MapGlobalLanguageToLocale(_settings?.Persisted?.GlobalLanguage);
            var locales = new[] { MinecraftAdvancementCatalog.DefaultLocale, locale }.Distinct().ToList();
            var uuids = MinecraftAccounts.Resolve(_providerSettings.PlayerUuid, source.GameFolders);

            var achievements = new List<AchievementDetail>();
            var tabArt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var instance in source.Instances)
            {
                cancel.ThrowIfCancellationRequested();
                var rows = BuildInstanceRows(game, instance, locale, locales, uuids, iconExtractor, tabArt, cancel);
                if (rows == null)
                {
                    return null;
                }

                achievements.AddRange(rows);
            }

            // Each instance without definitions has already logged why.
            if (achievements.Count == 0)
            {
                return null;
            }

            ApplyDefaultCategoryArt(game, tabArt);

            return new GameAchievementData
            {
                ProviderKey = MinecraftDataProvider.Key,
                ProviderGameKey = "java",
                RaritySource = RaritySource.Tier,
                LibrarySourceName = game.Source?.Name,
                GameName = game.Name,
                PlayniteGameId = game.Id,
                HasAchievements = achievements.Count > 0,
                Achievements = achievements,
                LastUpdatedUtc = DateTime.UtcNow
            };
        }

        /// <summary>
        /// One instance's rows: its worlds merged, keyed <c>instance|advancement</c> and categorized
        /// Instance > Tab. Null when a progress file is unreadable or no world has the player's file.
        /// </summary>
        private List<AchievementDetail> BuildInstanceRows(
            Game game,
            MinecraftInstance instance,
            string locale,
            IReadOnlyCollection<string> locales,
            ISet<string> uuids,
            MinecraftIconExtractor iconExtractor,
            IDictionary<string, string> tabArt,
            CancellationToken cancel)
        {
            var sources = LoadSources(instance, locales, cancel);
            var assetLanguage = MinecraftPaths.FindAssetLanguage(new[] { instance.GameFolder }, locale);
            var catalog = MinecraftAdvancementCatalog.Build(
                sources,
                assetLanguage == null ? null : new[] { assetLanguage },
                locale);
            if (catalog.Count == 0)
            {
                _logger?.Info($"[Minecraft] '{game.Name}': instance '{instance.Name}' has no advancement definitions (no game jar found); skipped.");
                return new List<AchievementDetail>();
            }

            var progress = MinecraftProgressReader.Read(
                instance.Worlds,
                uuids,
                message => _logger?.Warn($"[Minecraft] '{game.Name}': {message}; cached achievements preserved."),
                out var worldsRead);
            if (progress == null)
            {
                return null;
            }

            if (worldsRead == 0 && instance.Worlds.Any(HasAdvancementFiles))
            {
                _logger?.Warn(
                    $"[Minecraft] '{game.Name}': no advancement file in instance '{instance.Name}' belongs to " +
                    $"{(uuids.Count == 0 ? "a known account" : string.Join(", ", uuids))}; set the player UUID in Minecraft settings. Cached achievements preserved.");
                return null;
            }

            var rows = new List<AchievementDetail>();
            foreach (var advancement in OrderForDisplay(catalog))
            {
                progress.TryGetValue(advancement.Id, out var state);
                var iconPath = iconExtractor?.GetIconPath(advancement.IconItemId, advancement.IconCustomModelData, sources);
                catalog.TryGetValue(advancement.RootId ?? string.Empty, out var root);
                var category = CategoryPathHelper.JoinRaw(instance.Name, root?.Title);
                if (!string.IsNullOrWhiteSpace(category) && !tabArt.ContainsKey(category) && root != null)
                {
                    var rootIcon = iconExtractor?.GetIconPath(root.IconItemId, root.IconCustomModelData, sources);
                    if (rootIcon != null)
                    {
                        tabArt[category] = rootIcon;
                    }
                }

                var done = state?.Done == true;
                var detail = new AchievementDetail
                {
                    ApiName = instance.Key + "|" + advancement.Id,
                    DisplayName = string.IsNullOrWhiteSpace(advancement.Title) ? advancement.Id : advancement.Title,
                    Description = advancement.Description,
                    UnlockedIconPath = iconPath,
                    LockedIconPath = iconPath,
                    Hidden = advancement.Hidden,
                    Unlocked = done,
                    UnlockTimeUtc = done ? state.CompletedUtc : null,
                    GlobalPercentUnlocked = null,
                    Rarity = GetRarity(advancement),
                    CategoryKind = GetCategoryKind(advancement),
                    Category = category
                };

                ApplyProgress(detail, advancement, state);
                rows.Add(detail);
            }

            return rows;
        }

        /// <summary>
        /// Definition sources in ascending priority: the game jar, then mods, then world datapacks.
        /// </summary>
        private List<MinecraftResourceSource> LoadSources(MinecraftInstance instance, IReadOnlyCollection<string> locales, CancellationToken cancel)
        {
            return LoadSources(instance, locales, _logger, cancel);
        }

        internal static List<MinecraftResourceSource> LoadSources(
            MinecraftInstance instance,
            IReadOnlyCollection<string> locales,
            ILogger logger,
            CancellationToken cancel)
        {
            var paths = new List<string>();
            var gameJar = MinecraftPaths.FindGameJar(instance.GameFolder);
            if (gameJar != null)
            {
                paths.Add(gameJar);
            }

            paths.AddRange(MinecraftPaths.SafeFiles(Path.Combine(instance.GameFolder, "mods"), "*.jar")
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase));

            foreach (var world in instance.Worlds)
            {
                var datapacks = Path.Combine(world, "datapacks");
                paths.AddRange(MinecraftPaths.SafeFiles(datapacks, "*.zip")
                    .Concat(MinecraftPaths.SafeDirectories(datapacks))
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase));
            }

            var sources = new List<MinecraftResourceSource>();
            foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                cancel.ThrowIfCancellationRequested();
                var loaded = MinecraftResourceSource.Load(path, locales, logger);
                if (loaded != null)
                {
                    sources.Add(loaded);
                }
            }

            return sources;
        }

        /// <summary>
        /// Vanilla tabs in in-game order, then other tabs by title; each tab depth-first from its
        /// root with siblings by id.
        /// </summary>
        internal static List<MinecraftAdvancement> OrderForDisplay(IReadOnlyDictionary<string, MinecraftAdvancement> catalog)
        {
            var children = catalog.Values
                .Where(advancement => advancement.ParentId != null && catalog.ContainsKey(advancement.ParentId))
                .GroupBy(advancement => advancement.ParentId, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.OrderBy(advancement => advancement.Id, StringComparer.Ordinal).ToList(),
                    StringComparer.Ordinal);

            var tops = catalog.Values
                .Where(advancement => advancement.ParentId == null || !catalog.ContainsKey(advancement.ParentId))
                .OrderBy(advancement =>
                {
                    var rank = Array.IndexOf(VanillaRootOrder, advancement.Id);
                    return rank >= 0 ? rank : VanillaRootOrder.Length;
                })
                .ThenBy(advancement => advancement.Title ?? advancement.Id, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(advancement => advancement.Id, StringComparer.Ordinal);

            var ordered = new List<MinecraftAdvancement>();
            var visited = new HashSet<string>(StringComparer.Ordinal);
            foreach (var top in tops)
            {
                var stack = new Stack<MinecraftAdvancement>();
                stack.Push(top);
                while (stack.Count > 0)
                {
                    var current = stack.Pop();
                    if (!visited.Add(current.Id))
                    {
                        continue;
                    }

                    ordered.Add(current);
                    if (children.TryGetValue(current.Id, out var list))
                    {
                        for (var i = list.Count - 1; i >= 0; i--)
                        {
                            stack.Push(list[i]);
                        }
                    }
                }
            }

            // Parent cycles leave members unreachable from any top; keep them rather than drop them.
            ordered.AddRange(catalog.Values
                .Where(advancement => !visited.Contains(advancement.Id))
                .OrderBy(advancement => advancement.Id, StringComparer.Ordinal));
            return ordered;
        }

        /// <summary>
        /// Requirement groups met in the furthest-along world, for a locked visible advancement
        /// with more than one group.
        /// </summary>
        private static void ApplyProgress(AchievementDetail detail, MinecraftAdvancement advancement, MinecraftAdvancementProgress state)
        {
            if (detail.Unlocked || advancement.Hidden || state == null || advancement.Requirements.Count <= 1)
            {
                return;
            }

            var best = state.CriteriaByWorld
                .Select(criteria => advancement.Requirements.Count(group => group.Any(criteria.Contains)))
                .DefaultIfEmpty(0)
                .Max();
            if (best > 0)
            {
                detail.ProgressNum = best;
                detail.ProgressDenom = advancement.Requirements.Count;
            }
        }

        /// <summary>task, goal, visible challenge, hidden challenge in ascending tiers.</summary>
        internal static RarityTier GetRarity(MinecraftAdvancement advancement)
        {
            switch (advancement.Frame)
            {
                case "challenge": return advancement.Hidden ? RarityTier.UltraRare : RarityTier.Rare;
                case "goal": return RarityTier.Uncommon;
                default: return RarityTier.Common;
            }
        }

        /// <summary>Base for the minecraft namespace; no kind for mods and datapacks.</summary>
        internal static string GetCategoryKind(MinecraftAdvancement advancement)
        {
            return string.Equals(advancement.Namespace, MinecraftNamespace, StringComparison.Ordinal)
                ? "Base"
                : null;
        }

        private static bool HasAdvancementFiles(string world)
        {
            var folder = MinecraftPaths.GetAdvancementsFolder(world);
            return folder != null && MinecraftPaths.SafeFiles(folder, "*.json").Any();
        }

        /// <summary>
        /// Publishes each tab's root icon as the default art for its category. User overrides win.
        /// </summary>
        private void ApplyDefaultCategoryArt(Game game, IReadOnlyDictionary<string, string> tabArt)
        {
            if (tabArt.Count == 0 || game.Id == Guid.Empty)
            {
                return;
            }

            var diskImageService = PlayniteAchievementsPlugin.Instance?.DiskImageService;
            if (diskImageService == null)
            {
                return;
            }

            var gameIdText = game.Id.ToString("D");
            foreach (var entry in tabArt)
            {
                var label = AchievementCategoryHelper.NormalizeCategoryOrDefault(entry.Key);
                if (string.Equals(label, AchievementCategoryHelper.DefaultCategoryLabel, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    diskImageService.SaveDefaultCategoryImageFromFile(gameIdText, label, entry.Value);
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, $"[Minecraft] Default category image copy failed for '{entry.Key}'.");
                }
            }
        }
    }
}
