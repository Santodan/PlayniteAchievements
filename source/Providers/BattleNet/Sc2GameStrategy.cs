using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.BattleNet.Models;

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

        public bool MatchesGame(Game game)
        {
            return BattleNetGameSupport.IsSc2Enabled && BattleNetGameSupport.IsSc2Game(game);
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
                ct);
            var profile = await _client.GetSc2ProfileAsync(
                regionId,
                realmId,
                profileId,
                settings.BattleNetClientId,
                settings.BattleNetClientSecret,
                apiLocale,
                ct);

            if (definitions == null || profile == null)
            {
                _logger?.Warn($"[BattleNet/SC2] Missing SC2 API data. definitions={Bool(definitions != null)}, profile={Bool(profile != null)}");
                return null;
            }

            var categoryLookup = definitions.Categories?.ToDictionary(c => c.Id, c => c.Name) ?? new Dictionary<string, string>();
            var parentLookup = definitions.Categories?.ToDictionary(c => c.Id, c => c.ParentCategoryId) ?? new Dictionary<string, string>();
            var earnedLookup = profile.EarnedAchievements?.ToDictionary(e => e.AchievementId) ?? new Dictionary<string, Sc2EarnedAchievement>();

            var achievements = new List<AchievementDetail>();

            foreach (var def in definitions.Achievements ?? Enumerable.Empty<Sc2AchievementDefinition>())
            {
                var detail = new AchievementDetail
                {
                    ApiName = def.Id,
                    DisplayName = def.Title,
                    Description = def.Description,
                    UnlockedIconPath = def.ImageUrl,
                    Points = def.Points > 0 ? def.Points : (int?)null,
                    Category = ResolveCategory(def.CategoryId, categoryLookup, parentLookup),
                    ProviderKey = "BattleNet"
                };

                if (earnedLookup.TryGetValue(def.Id, out var earned))
                {
                    detail.Unlocked = earned.IsComplete;
                    if (earned.IsComplete && int.TryParse(earned.CompletionDate, out var unixSeconds) && unixSeconds > 0)
                    {
                        detail.UnlockTimeUtc = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;
                    }
                }
                else
                {
                    detail.Unlocked = false;
                }

                achievements.Add(detail);
            }

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

            var chosen = profiles?.FirstOrDefault(p => p != null && p.ProfileId > 0);
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
                _logger?.Info($"[BattleNet/SC2] Discovered {profiles.Count} profiles; using first (region={chosen.RegionId}, realm={chosen.RealmId}).");
            }

            return true;
        }

        private static string ResolveCategory(string categoryId, Dictionary<string, string> names, Dictionary<string, string> parents)
        {
            if (string.IsNullOrEmpty(categoryId) || !names.TryGetValue(categoryId, out var name))
                return null;

            if (parents.TryGetValue(categoryId, out var parentId) &&
                !string.IsNullOrEmpty(parentId) &&
                names.TryGetValue(parentId, out var parentName))
            {
                return $"{parentName} - {name}";
            }

            return name;
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
