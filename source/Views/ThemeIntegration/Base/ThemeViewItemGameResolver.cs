using Playnite.SDK.Models;
using System;
using System.Collections.Concurrent;

namespace PlayniteAchievements.Views.ThemeIntegration.Base
{
    internal static class ThemeViewItemGameResolver
    {
        private static readonly string[] GamePropertyCandidates =
        {
            "Game",
            "Source",
            "Item",
            "SourceItem",
            "Value"
        };

        private static readonly ConcurrentDictionary<string, string> GamePropertyCache =
            new ConcurrentDictionary<string, string>();

        public static Game GetGame(object dataContext)
        {
            if (dataContext == null)
            {
                return null;
            }

            if (dataContext is Game game)
            {
                return game;
            }

            var type = dataContext.GetType();
            var typeKey = type.FullName ?? type.GUID.ToString("D");
            if (GamePropertyCache.TryGetValue(typeKey, out var cachedPropertyName))
            {
                if (string.IsNullOrWhiteSpace(cachedPropertyName))
                {
                    return null;
                }

                if (TryGetGamePropertyValue(dataContext, cachedPropertyName, out var cachedGame))
                {
                    return cachedGame;
                }
            }

            for (var i = 0; i < GamePropertyCandidates.Length; i++)
            {
                var propertyName = GamePropertyCandidates[i];
                if (TryGetGamePropertyValue(dataContext, propertyName, out var wrappedGame))
                {
                    GamePropertyCache.TryAdd(typeKey, propertyName);
                    return wrappedGame;
                }
            }

            GamePropertyCache.TryAdd(typeKey, string.Empty);
            return null;
        }

        private static bool TryGetGamePropertyValue(object source, string propertyName, out Game game)
        {
            game = null;
            if (source == null || string.IsNullOrWhiteSpace(propertyName))
            {
                return false;
            }

            var property = source.GetType().GetProperty(propertyName);
            if (property == null || property.GetIndexParameters().Length != 0)
            {
                return false;
            }

            object propertyValue;
            try
            {
                propertyValue = property.GetValue(source);
            }
            catch
            {
                return false;
            }

            if (propertyValue is Game directGame)
            {
                game = directGame;
                return true;
            }

            if (propertyValue == null || ReferenceEquals(propertyValue, source))
            {
                return false;
            }

            var nestedGameProperty = propertyValue.GetType().GetProperty("Game");
            if (nestedGameProperty == null || nestedGameProperty.GetIndexParameters().Length != 0)
            {
                return false;
            }

            try
            {
                game = nestedGameProperty.GetValue(propertyValue) as Game;
            }
            catch
            {
                game = null;
            }

            return game != null;
        }
    }
}
