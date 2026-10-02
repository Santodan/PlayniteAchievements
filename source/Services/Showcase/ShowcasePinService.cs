using System;
using System.Collections.Generic;
using System.Linq;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Services.Showcase
{
    public static partial class ShowcasePinService
    {
        public static PinnedAchievementCollection ResolveAchievementCollection(
            ShowcaseSettings settings,
            string collectionId = null)
        {
            EnsureCollections(settings);
            var collections = settings?.AchievementPinCollections;
            if (collections == null || collections.Count == 0)
            {
                return null;
            }

            return collections.FirstOrDefault(collection =>
                       collection != null &&
                       string.Equals(collection.CollectionId, collectionId, StringComparison.OrdinalIgnoreCase))
                   ?? collections.FirstOrDefault(collection =>
                       collection != null &&
                       string.Equals(
                           collection.CollectionId,
                           settings.DefaultAchievementPinCollectionId,
                           StringComparison.OrdinalIgnoreCase))
                   ?? collections.FirstOrDefault(collection => collection != null);
        }

        public static PinnedGameCollection ResolveGameCollection(
            ShowcaseSettings settings,
            string collectionId = null)
        {
            EnsureCollections(settings);
            var collections = settings?.GamePinCollections;
            if (collections == null || collections.Count == 0)
            {
                return null;
            }

            return collections.FirstOrDefault(collection =>
                       collection != null &&
                       string.Equals(collection.CollectionId, collectionId, StringComparison.OrdinalIgnoreCase))
                   ?? collections.FirstOrDefault(collection =>
                       collection != null &&
                       string.Equals(
                           collection.CollectionId,
                           settings.DefaultGamePinCollectionId,
                           StringComparison.OrdinalIgnoreCase))
                   ?? collections.FirstOrDefault(collection => collection != null);
        }

        public static bool IsDefaultAchievementCollection(ShowcaseSettings settings, string collectionId) =>
            settings != null &&
            !string.IsNullOrWhiteSpace(collectionId) &&
            string.Equals(
                settings.DefaultAchievementPinCollectionId,
                collectionId,
                StringComparison.OrdinalIgnoreCase);

        public static bool IsDefaultGameCollection(ShowcaseSettings settings, string collectionId) =>
            settings != null &&
            !string.IsNullOrWhiteSpace(collectionId) &&
            string.Equals(
                settings.DefaultGamePinCollectionId,
                collectionId,
                StringComparison.OrdinalIgnoreCase);

        public static bool TryCreateAchievementCollection(
            ShowcaseSettings settings,
            string name,
            out PinnedAchievementCollection collection)
        {
            collection = null;
            var normalized = NormalizeName(name);
            if (settings == null || normalized == null)
            {
                return false;
            }

            EnsureCollections(settings);
            if (settings.AchievementPinCollections.Any(candidate =>
                string.Equals(candidate?.Name, normalized, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            collection = new PinnedAchievementCollection { Name = normalized };
            settings.AchievementPinCollections.Add(collection);
            return true;
        }

        public static bool TryCreateGameCollection(
            ShowcaseSettings settings,
            string name,
            out PinnedGameCollection collection)
        {
            collection = null;
            var normalized = NormalizeName(name);
            if (settings == null || normalized == null)
            {
                return false;
            }

            EnsureCollections(settings);
            if (settings.GamePinCollections.Any(candidate =>
                string.Equals(candidate?.Name, normalized, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            collection = new PinnedGameCollection { Name = normalized };
            settings.GamePinCollections.Add(collection);
            return true;
        }

        public static bool RenameAchievementCollection(
            ShowcaseSettings settings,
            string collectionId,
            string name)
        {
            EnsureCollections(settings);
            var collection = FindAchievementCollection(settings, collectionId);
            var normalized = NormalizeName(name);
            if (collection == null ||
                normalized == null ||
                settings.AchievementPinCollections.Any(candidate =>
                    candidate != null &&
                    !string.Equals(candidate.CollectionId, collection.CollectionId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(candidate.Name, normalized, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            collection.Name = normalized;
            return true;
        }

        public static bool RenameGameCollection(
            ShowcaseSettings settings,
            string collectionId,
            string name)
        {
            EnsureCollections(settings);
            var collection = FindGameCollection(settings, collectionId);
            var normalized = NormalizeName(name);
            if (collection == null ||
                normalized == null ||
                settings.GamePinCollections.Any(candidate =>
                    candidate != null &&
                    !string.Equals(candidate.CollectionId, collection.CollectionId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(candidate.Name, normalized, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            collection.Name = normalized;
            return true;
        }

        public static bool ClearAchievementCollection(ShowcaseSettings settings, string collectionId)
        {
            EnsureCollections(settings);
            var collection = FindAchievementCollection(settings, collectionId);
            if (collection == null)
            {
                return false;
            }

            collection.Pins = new List<PinnedAchievementReference>();
            return true;
        }

        public static bool ClearGameCollection(ShowcaseSettings settings, string collectionId)
        {
            EnsureCollections(settings);
            var collection = FindGameCollection(settings, collectionId);
            if (collection == null)
            {
                return false;
            }

            collection.GameIds = new List<Guid>();
            return true;
        }

        public static bool DeleteAchievementCollection(ShowcaseSettings settings, string collectionId)
        {
            if (settings == null || IsDefaultAchievementCollection(settings, collectionId))
            {
                return false;
            }

            var collection = settings.AchievementPinCollections?.FirstOrDefault(candidate =>
                candidate != null &&
                string.Equals(candidate.CollectionId, collectionId, StringComparison.OrdinalIgnoreCase));
            if (collection == null || !settings.AchievementPinCollections.Remove(collection))
            {
                return false;
            }

            ReassignDeletedCollection(settings, collectionId, achievementCollection: true);
            return true;
        }

        public static bool DeleteGameCollection(ShowcaseSettings settings, string collectionId)
        {
            if (settings == null || IsDefaultGameCollection(settings, collectionId))
            {
                return false;
            }

            var collection = settings.GamePinCollections?.FirstOrDefault(candidate =>
                candidate != null &&
                string.Equals(candidate.CollectionId, collectionId, StringComparison.OrdinalIgnoreCase));
            if (collection == null || !settings.GamePinCollections.Remove(collection))
            {
                return false;
            }

            ReassignDeletedCollection(settings, collectionId, achievementCollection: false);
            return true;
        }

        public static bool IsGamePinned(ShowcaseSettings settings, string collectionId, Guid gameId)
        {
            return gameId != Guid.Empty &&
                ResolveGameCollection(settings, collectionId)?.GameIds?.Contains(gameId) == true;
        }

        public static bool IsGamePinned(ShowcaseSettings settings, Guid gameId) =>
            IsGamePinned(settings, settings?.DefaultGamePinCollectionId, gameId);

        public static bool ToggleGame(ShowcaseSettings settings, string collectionId, Guid gameId)
        {
            var collection = ResolveGameCollection(settings, collectionId);
            if (collection == null || gameId == Guid.Empty)
            {
                return false;
            }

            collection.GameIds = collection.GameIds ?? new List<Guid>();
            if (collection.GameIds.Remove(gameId))
            {
                return false;
            }

            collection.GameIds.Add(gameId);
            return true;
        }

        public static bool ToggleGame(ShowcaseSettings settings, Guid gameId) =>
            ToggleGame(settings, settings?.DefaultGamePinCollectionId, gameId);

        public static bool MoveGame(
            ShowcaseSettings settings,
            string collectionId,
            Guid gameId,
            int direction)
        {
            var pins = ResolveGameCollection(settings, collectionId)?.GameIds;
            if (pins == null || direction == 0)
            {
                return false;
            }

            var index = pins.IndexOf(gameId);
            var target = Math.Max(0, Math.Min(pins.Count - 1, index + Math.Sign(direction)));
            if (index < 0 || target == index)
            {
                return false;
            }

            pins.RemoveAt(index);
            pins.Insert(target, gameId);
            return true;
        }

        public static bool MoveGame(ShowcaseSettings settings, Guid gameId, int direction) =>
            MoveGame(settings, settings?.DefaultGamePinCollectionId, gameId, direction);

        public static bool IsAchievementPinned(
            ShowcaseSettings settings,
            string collectionId,
            Guid gameId,
            string apiName)
        {
            return gameId != Guid.Empty &&
                !string.IsNullOrWhiteSpace(apiName) &&
                ResolveAchievementCollection(settings, collectionId)?.Pins?.Any(pin =>
                    pin != null &&
                    pin.GameId == gameId &&
                    string.Equals(pin.ApiName, apiName, StringComparison.OrdinalIgnoreCase)) == true;
        }

        public static bool IsAchievementPinned(
            ShowcaseSettings settings,
            Guid gameId,
            string apiName) =>
            IsAchievementPinned(settings, settings?.DefaultAchievementPinCollectionId, gameId, apiName);

        public static bool ToggleAchievement(
            ShowcaseSettings settings,
            string collectionId,
            Guid gameId,
            string apiName,
            string gameName,
            string achievementName)
        {
            var collection = ResolveAchievementCollection(settings, collectionId);
            if (collection == null || gameId == Guid.Empty || string.IsNullOrWhiteSpace(apiName))
            {
                return false;
            }

            collection.Pins = collection.Pins ?? new List<PinnedAchievementReference>();
            var existing = collection.Pins.FirstOrDefault(pin =>
                pin != null &&
                pin.GameId == gameId &&
                string.Equals(pin.ApiName, apiName, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                collection.Pins.Remove(existing);
                return false;
            }

            collection.Pins.Add(new PinnedAchievementReference
            {
                GameId = gameId,
                ApiName = apiName.Trim(),
                LastKnownGameName = gameName,
                LastKnownAchievementName = achievementName,
                AddedUtc = DateTime.UtcNow
            });
            return true;
        }

        public static bool ToggleAchievement(
            ShowcaseSettings settings,
            Guid gameId,
            string apiName,
            string gameName,
            string achievementName) =>
            ToggleAchievement(
                settings,
                settings?.DefaultAchievementPinCollectionId,
                gameId,
                apiName,
                gameName,
                achievementName);

        public static bool MoveAchievement(
            ShowcaseSettings settings,
            string collectionId,
            Guid gameId,
            string apiName,
            int direction)
        {
            var pins = ResolveAchievementCollection(settings, collectionId)?.Pins;
            if (pins == null || direction == 0)
            {
                return false;
            }

            var pin = pins.FirstOrDefault(candidate =>
                candidate != null &&
                candidate.GameId == gameId &&
                string.Equals(candidate.ApiName, apiName, StringComparison.OrdinalIgnoreCase));
            var index = pin == null ? -1 : pins.IndexOf(pin);
            var target = Math.Max(0, Math.Min(pins.Count - 1, index + Math.Sign(direction)));
            if (index < 0 || target == index)
            {
                return false;
            }

            pins.RemoveAt(index);
            pins.Insert(target, pin);
            return true;
        }

        public static bool MoveAchievement(
            ShowcaseSettings settings,
            Guid gameId,
            string apiName,
            int direction) =>
            MoveAchievement(
                settings,
                settings?.DefaultAchievementPinCollectionId,
                gameId,
                apiName,
                direction);

        private static void EnsureCollections(ShowcaseSettings settings)
        {
            if (settings == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(settings.DefaultAchievementPinCollectionId))
            {
                settings.DefaultAchievementPinCollectionId = ShowcaseSettings.BuiltInAchievementCollectionId;
            }

            if (string.IsNullOrWhiteSpace(settings.DefaultGamePinCollectionId))
            {
                settings.DefaultGamePinCollectionId = ShowcaseSettings.BuiltInGameCollectionId;
            }

            settings.AchievementPinCollections = settings.AchievementPinCollections ??
                new List<PinnedAchievementCollection>();
            if (!settings.AchievementPinCollections.Any(collection =>
                collection != null &&
                string.Equals(
                    collection.CollectionId,
                    settings.DefaultAchievementPinCollectionId,
                    StringComparison.OrdinalIgnoreCase)))
            {
                settings.AchievementPinCollections.Insert(0, new PinnedAchievementCollection
                {
                    CollectionId = settings.DefaultAchievementPinCollectionId,
                    Name = "Default"
                });
            }

            settings.GamePinCollections = settings.GamePinCollections ?? new List<PinnedGameCollection>();
            if (!settings.GamePinCollections.Any(collection =>
                collection != null &&
                string.Equals(
                    collection.CollectionId,
                    settings.DefaultGamePinCollectionId,
                    StringComparison.OrdinalIgnoreCase)))
            {
                settings.GamePinCollections.Insert(0, new PinnedGameCollection
                {
                    CollectionId = settings.DefaultGamePinCollectionId,
                    Name = "Default"
                });
            }
        }

        private static string NormalizeName(string name) =>
            string.IsNullOrWhiteSpace(name) ? null : name.Trim();

        private static PinnedAchievementCollection FindAchievementCollection(
            ShowcaseSettings settings,
            string collectionId) =>
            string.IsNullOrWhiteSpace(collectionId)
                ? null
                : settings?.AchievementPinCollections?.FirstOrDefault(collection =>
                    collection != null &&
                    string.Equals(
                        collection.CollectionId,
                        collectionId,
                        StringComparison.OrdinalIgnoreCase));

        private static PinnedGameCollection FindGameCollection(
            ShowcaseSettings settings,
            string collectionId) =>
            string.IsNullOrWhiteSpace(collectionId)
                ? null
                : settings?.GamePinCollections?.FirstOrDefault(collection =>
                    collection != null &&
                    string.Equals(
                        collection.CollectionId,
                        collectionId,
                        StringComparison.OrdinalIgnoreCase));

        private static void ReassignDeletedCollection(
            ShowcaseSettings settings,
            string deletedCollectionId,
            bool achievementCollection)
        {
            var replacement = achievementCollection
                ? settings.DefaultAchievementPinCollectionId
                : settings.DefaultGamePinCollectionId;
            foreach (var instance in EnumerateInstances(settings))
            {
                if (!UsesCollectionType(instance?.Kind ?? default(ShowcaseWidgetKind), achievementCollection) ||
                    !string.Equals(
                        ShowcaseWidgetOptions.GetPinCollectionId(instance),
                        deletedCollectionId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                ShowcaseWidgetOptions.SetPinCollectionId(instance, replacement);
            }
        }

        private static IEnumerable<ShowcaseWidgetInstanceSettings> EnumerateInstances(
            ShowcaseSettings settings)
        {
            foreach (var instance in settings?.WidgetInstances ??
                new List<ShowcaseWidgetInstanceSettings>())
            {
                yield return instance;
            }

            foreach (var instance in (settings?.StartPageInstances ??
                new Dictionary<string, ShowcaseWidgetInstanceSettings>()).Values)
            {
                yield return instance;
            }
        }

        private static bool UsesCollectionType(ShowcaseWidgetKind kind, bool achievementCollection)
        {
            // Kinds whose options can reference a pin collection of the given type; the exact
            // id match in the caller does the precise work, this just keeps unrelated kinds
            // untouched. The mosaic and the slideshow can reference either type.
            switch (kind)
            {
                case ShowcaseWidgetKind.IconMosaic:
                case ShowcaseWidgetKind.ScreenshotSlideshow:
                    return true;
                case ShowcaseWidgetKind.RecentAchievements:
                    return achievementCollection;
                case ShowcaseWidgetKind.GameSummaries:
                    return !achievementCollection;
                default:
                    return false;
            }
        }
    }
}
