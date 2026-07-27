using Playnite.SDK.Models;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Achievements;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.EA
{
    internal static class EAProviderSupport
    {
        private static readonly string[] SourceNames =
        {
            "EA",
            "EA app",
            "Origin",
            "Electronic Arts"
        };

        public static bool IsEaCapable(Game game, Guid eaPluginId)
        {
            if (game == null)
            {
                return false;
            }

            if (eaPluginId != Guid.Empty && game.PluginId == eaPluginId)
            {
                return true;
            }

            return IsEaSourceName(game.Source?.Name);
        }

        public static bool IsEaSourceName(string sourceName)
        {
            var normalized = NormalizeText(sourceName);
            return !string.IsNullOrWhiteSpace(normalized) &&
                SourceNames.Any(candidate => string.Equals(
                    NormalizeText(candidate),
                    normalized,
                    StringComparison.OrdinalIgnoreCase));
        }

        public static string ExtractOfferIdFromGameId(string gameId)
        {
            if (string.IsNullOrWhiteSpace(gameId))
            {
                return null;
            }

            var trimmed = gameId.Trim();
            var prefixes = new[] { "Origin.", "EA." };
            foreach (var prefix in prefixes)
            {
                if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    var offerId = trimmed.Substring(prefix.Length).Trim();
                    return string.IsNullOrWhiteSpace(offerId) ? null : offerId;
                }
            }

            return trimmed;
        }

        public static IReadOnlyList<string> BuildOfferIdCandidates(string matchedOfferId, string gameId)
        {
            var candidates = new List<string>();

            AddCandidate(candidates, matchedOfferId);
            AddCandidate(candidates, ExtractOfferIdFromGameId(matchedOfferId));

            var extractedGameId = ExtractOfferIdFromGameId(gameId);
            AddCandidate(candidates, extractedGameId);
            AddCandidate(candidates, gameId);

            if (IsUnprefixedOfferId(extractedGameId))
            {
                AddCandidate(candidates, "Origin." + extractedGameId);
            }

            return candidates;
        }

        public static EaOwnedGame MatchGame(IEnumerable<EaOwnedGame> ownedGames, Game game, string gameId)
        {
            var games = ownedGames?.Where(item => item != null).ToList();
            if (games == null || games.Count == 0)
            {
                return null;
            }

            var rawGameId = gameId?.Trim();
            var extractedOfferId = ExtractOfferIdFromGameId(gameId);
            var idCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddCandidate(idCandidates, rawGameId);
            AddCandidate(idCandidates, extractedOfferId);

            if (idCandidates.Count > 0)
            {
                var byOfferId = games.FirstOrDefault(g => idCandidates.Contains(g.OriginOfferId?.Trim()));
                if (byOfferId != null)
                {
                    return byOfferId;
                }

                var bySlug = games.FirstOrDefault(g => idCandidates.Contains(g.GameSlug?.Trim()));
                if (bySlug != null)
                {
                    return bySlug;
                }
            }

            var gameName = NormalizeText(game?.Name);
            if (string.IsNullOrWhiteSpace(gameName))
            {
                return null;
            }

            return games.FirstOrDefault(g =>
                string.Equals(NormalizeText(g.ProductName), gameName, StringComparison.OrdinalIgnoreCase));
        }

        public static GameAchievementData MapToGameData(Game game, IEnumerable<EaAchievementItem> items)
        {
            var data = new GameAchievementData
            {
                AppId = 0,
                GameName = game?.Name,
                ProviderKey = "EA",
                LibrarySourceName = game?.Source?.Name,
                LastUpdatedUtc = DateTime.UtcNow,
                HasAchievements = false,
                PlayniteGameId = game != null ? game.Id : Guid.Empty,
                Achievements = new List<AchievementDetail>()
            };

            foreach (var item in items ?? Enumerable.Empty<EaAchievementItem>())
            {
                if (item == null || string.IsNullOrWhiteSpace(item.AchievementId))
                {
                    continue;
                }

                data.Achievements.Add(new AchievementDetail
                {
                    ApiName = item.AchievementId,
                    DisplayName = string.IsNullOrWhiteSpace(item.Title) ? item.AchievementId : item.Title,
                    Description = item.Description ?? string.Empty,
                    UnlockedIconPath = string.Empty,
                    LockedIconPath = string.Empty,
                    Points = null,
                    Category = null,
                    Hidden = false,
                    Unlocked = item.IsUnlocked,
                    UnlockTimeUtc = NormalizeUtc(item.UnlockTimeUtc),
                    Rarity = RarityTier.Common,
                    GlobalPercentUnlocked = null
                });
            }

            data.HasAchievements = data.Achievements.Count > 0;
            return data;
        }

        public static DateTime? NormalizeUtc(DateTime? value)
        {
            if (!value.HasValue)
            {
                return null;
            }

            if (value.Value.Kind == DateTimeKind.Utc)
            {
                return value.Value;
            }

            if (value.Value.Kind == DateTimeKind.Unspecified)
            {
                return DateTime.SpecifyKind(value.Value, DateTimeKind.Utc);
            }

            return value.Value.ToUniversalTime();
        }

        public static bool IsTransientError(Exception ex)
        {
            return TransientErrorClassifier.IsTransient(ex, e =>
                e is EaTransientException ? true :
                e is TaskCanceledException ? true :
                e is EaApiHttpException httpEx ? TransientErrorClassifier.IsTransientStatusCode((int)httpEx.StatusCode) :
                (bool?)null);
        }

        private static void AddCandidate(HashSet<string> candidates, string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                candidates.Add(value.Trim());
            }
        }

        private static void AddCandidate(ICollection<string> candidates, string value)
        {
            var normalized = value?.Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return;
            }

            if (!candidates.Any(candidate => string.Equals(candidate, normalized, StringComparison.OrdinalIgnoreCase)))
            {
                candidates.Add(normalized);
            }
        }

        private static bool IsUnprefixedOfferId(string value)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                value.Trim().StartsWith("OFR.", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeText(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return Regex.Replace(value.Trim(), @"\s+", " ");
        }
    }
}
