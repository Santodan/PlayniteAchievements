using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.BattleNet.Models;
using PlayniteAchievements.Services.Achievements;

namespace PlayniteAchievements.Providers.BattleNet
{
    internal sealed class Sc2GameStrategy
    {
        private readonly BattleNetApiClient _client;
        private readonly ILogger _logger;

        public Sc2GameStrategy(BattleNetApiClient client, ILogger logger)
        {
            _client = client;
            _logger = logger;
        }

        public async Task<GameAchievementData> FetchAchievementsAsync(Game game, string locale, CancellationToken ct)
        {
            var settings = ProviderRegistry.Settings<BattleNetSettings>();
            var effectiveLocale = string.IsNullOrWhiteSpace(locale) ? "en-US" : locale;
            var apiLocale = BattleNetLocaleMapper.ToApiLocale(effectiveLocale);

            if (!BattleNetGameSupport.HasConfiguredSc2(settings) &&
                !await TryDiscoverProfileAsync(settings, ct).ConfigureAwait(false))
            {
                _logger?.Warn($"[BattleNet/SC2] SC2 profile unavailable. credentials={Bool(BattleNetGameSupport.HasApiCredentials(settings))}, oauth={Bool(BattleNetGameSupport.HasOAuthSession(settings))}");
                return null;
            }

            var regionId = settings.Sc2RegionId;
            var realmId = settings.Sc2RealmId;
            var profileId = settings.Sc2ProfileId;

            var definitions = await _client.GetSc2AchievementDefinitionsAsync(
                regionId,
                settings.BattleNetClientId,
                settings.BattleNetClientSecret,
                apiLocale,
                ct).ConfigureAwait(false);
            var profile = await _client.GetSc2ProfileAsync(
                regionId,
                realmId,
                profileId,
                settings.BattleNetClientId,
                settings.BattleNetClientSecret,
                apiLocale,
                ct).ConfigureAwait(false);

            if (definitions == null || profile == null)
            {
                _logger?.Warn($"[BattleNet/SC2] Missing SC2 API data. definitions={Bool(definitions != null)}, profile={Bool(profile != null)}");
                return null;
            }

            var achievements = BuildAchievements(definitions, profile);

            var data = new GameAchievementData
            {
                ProviderKey = "BattleNet",
                GameName = game.Name,
                PlayniteGameId = game.Id,
                AppId = StableAppId("StarCraft2"),
                Achievements = achievements,
                LastUpdatedUtc = DateTime.UtcNow,
                HasAchievements = achievements.Count > 0
            };

            return data;
        }

        /// <summary>
        /// Maps the static achievement catalog onto the profile's earned list. Every catalog entry
        /// becomes a row; an entry is unlocked when the profile reports it complete.
        /// </summary>
        internal static List<AchievementDetail> BuildAchievements(
            Sc2AchievementDefinitionsResponse definitions,
            Sc2ProfileResponse profile)
        {
            var categories = (definitions?.Categories ?? new List<Sc2Category>())
                .Where(c => c != null && !string.IsNullOrEmpty(c.Id))
                .GroupBy(c => c.Id, StringComparer.Ordinal)
                .Select(g => g.First())
                .ToList();
            var categoryLookup = categories.ToDictionary(c => c.Id, c => c.Name, StringComparer.Ordinal);
            var parentLookup = categories.ToDictionary(c => c.Id, c => c.ParentCategoryId, StringComparer.Ordinal);
            var earnedLookup = (profile?.EarnedAchievements ?? new List<Sc2EarnedAchievement>())
                .Where(e => e != null && !string.IsNullOrEmpty(e.AchievementId))
                .GroupBy(e => e.AchievementId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.FirstOrDefault(e => e.IsComplete) ?? g.First(), StringComparer.Ordinal);

            var achievements = new List<AchievementDetail>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var def in definitions?.Achievements ?? Enumerable.Empty<Sc2AchievementDefinition>())
            {
                if (def == null || string.IsNullOrEmpty(def.Id) || !seen.Add(def.Id))
                {
                    continue;
                }

                var detail = new AchievementDetail
                {
                    ApiName = def.Id,
                    DisplayName = def.Title,
                    Description = def.Description,
                    UnlockedIconPath = def.ImageUrl,
                    Points = def.Points > 0 ? def.Points : (int?)null,
                    Category = ResolveCategory(def.CategoryId, categoryLookup, parentLookup),
                    ProviderKey = "BattleNet",
                    Unlocked = false
                };

                if (earnedLookup.TryGetValue(def.Id, out var earned) && earned.IsComplete)
                {
                    detail.Unlocked = true;
                    detail.UnlockTimeUtc = ToUtc(earned.CompletionDate);
                }

                achievements.Add(detail);
            }

            return achievements;
        }

        internal static DateTime? ToUtc(double? unixSeconds)
        {
            if (!unixSeconds.HasValue ||
                double.IsNaN(unixSeconds.Value) ||
                double.IsInfinity(unixSeconds.Value) ||
                unixSeconds.Value <= 0 ||
                unixSeconds.Value > 253402300799d)
            {
                return null;
            }

            return DateTimeOffset.FromUnixTimeSeconds((long)unixSeconds.Value).UtcDateTime;
        }

        /// <summary>
        /// Resolves the account's StarCraft II profile coordinates from the Battle.net account using the
        /// OAuth session, then persists them so later refreshes skip the lookup. Returns false when the
        /// account lacks API credentials, an OAuth session, or any StarCraft II profile.
        /// </summary>
        private async Task<bool> TryDiscoverProfileAsync(BattleNetSettings settings, CancellationToken ct)
        {
            if (!BattleNetGameSupport.HasApiCredentials(settings) || !BattleNetGameSupport.HasOAuthSession(settings))
            {
                return false;
            }

            var apiRegion = string.IsNullOrWhiteSpace(settings.WowRegion) ? "us" : settings.WowRegion;
            var profiles = await _client.GetSc2PlayerProfilesAsync(
                apiRegion,
                settings.BattleNetAccountId,
                settings.BattleNetAccessToken,
                ct).ConfigureAwait(false);

            var chosen = ChooseProfile(profiles, apiRegion);
            if (chosen == null)
            {
                return false;
            }

            if (chosen.RegionId > 0)
            {
                settings.Sc2RegionId = chosen.RegionId;
            }
            if (chosen.RealmId > 0)
            {
                settings.Sc2RealmId = chosen.RealmId;
            }
            settings.Sc2ProfileId = chosen.ProfileId;
            ProviderRegistry.Write(settings, persistToDisk: true);

            if (profiles.Count > 1)
            {
                _logger?.Info($"[BattleNet/SC2] Discovered {profiles.Count} profiles; using region={chosen.RegionId}, realm={chosen.RealmId}.");
            }

            return true;
        }

        /// <summary>
        /// Picks the profile in the account's own API region when there is one, so an account with
        /// profiles in several regions reads the one it signed in through; otherwise the first.
        /// </summary>
        internal static Sc2PlayerProfile ChooseProfile(IEnumerable<Sc2PlayerProfile> profiles, string apiRegion)
        {
            var usable = (profiles ?? Enumerable.Empty<Sc2PlayerProfile>())
                .Where(p => p != null && p.ProfileId > 0)
                .ToList();
            return usable.FirstOrDefault(p => string.Equals(
                    BattleNetApiClient.MapSc2RegionIdToApiRegion(p.RegionId),
                    apiRegion,
                    StringComparison.OrdinalIgnoreCase))
                ?? usable.FirstOrDefault();
        }

        private static string ResolveCategory(string categoryId, Dictionary<string, string> names, Dictionary<string, string> parents)
        {
            if (string.IsNullOrEmpty(categoryId) || !names.TryGetValue(categoryId, out var name))
                return null;

            // Walk to the root rather than taking a single hop, so a category nested deeper than
            // one level keeps its whole ancestry. The visited set guards against a cycle in the
            // upstream parent links; NormalizePath applies the depth cap.
            var segments = new List<string> { name };
            var visited = new HashSet<string>(StringComparer.Ordinal) { categoryId };
            var currentId = categoryId;

            while (parents.TryGetValue(currentId, out var parentId) &&
                   !string.IsNullOrEmpty(parentId) &&
                   visited.Add(parentId) &&
                   names.TryGetValue(parentId, out var parentName))
            {
                segments.Insert(0, parentName);
                currentId = parentId;
            }

            return CategoryPathHelper.JoinRaw(segments.ToArray());
        }

        private static int StableAppId(string id)
        {
            int hash = 0;
            foreach (char c in id)
            {
                hash = (hash << 5) - hash + c;
            }
            return Math.Abs(hash);
        }

        private static string Bool(bool value) => value ? "true" : "false";
    }
}
