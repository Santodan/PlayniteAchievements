using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Models;
using PlayniteAchievements.Providers;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services
{
    internal static class CustomRefreshGameMatcher
    {
        internal const string SteamFamilySharingSelectionKey = "SteamFamilySharing";
        private const string LocalProviderKey = "Local";
        private static readonly string[] DefaultLocalExcludedSelectionKeys =
        {
            SteamRefreshTargeting.SteamProviderKey,
            SteamFamilySharingSelectionKey,
            "Epic",
            "GOG",
            "PSN",
            "Xbox",
            "RetroAchievements",
            "ShadPS4",
            "Xenia",
            "RPCS3",
            "Manual",
            "Exophase"
        };

        public static bool MatchesSelectionOption(
            Game game,
            IDataProvider resolvedProvider,
            string selectionKey,
            string selectionName,
            IEnumerable<string> knownSourceNames = null)
        {
            if (game == null || resolvedProvider == null || string.IsNullOrWhiteSpace(selectionKey))
            {
                return false;
            }

            if (string.Equals(selectionKey, SteamFamilySharingSelectionKey, StringComparison.OrdinalIgnoreCase))
            {
                return MatchesProviderSelection(
                    game,
                    resolvedProvider,
                    SteamRefreshTargeting.SteamProviderKey,
                    SteamRefreshTargeting.SteamProviderKey,
                    SteamRefreshTargetMode.FamilySharedOnly,
                    knownSourceNames);
            }

            var steamMode = string.Equals(selectionKey, SteamRefreshTargeting.SteamProviderKey, StringComparison.OrdinalIgnoreCase)
                ? SteamRefreshTargetMode.OwnedOnly
                : SteamRefreshTargetMode.All;

            return MatchesProviderSelection(game, resolvedProvider, selectionKey, selectionName, steamMode, knownSourceNames);
        }

        public static bool MatchesProviderSelection(
            Game game,
            IDataProvider resolvedProvider,
            IEnumerable<IDataProvider> selectedProviders,
            SteamRefreshTargetMode steamTargetMode,
            IEnumerable<string> knownSourceNames = null)
        {
            if (game == null || resolvedProvider == null || selectedProviders == null)
            {
                return false;
            }

            var selectedProvider = selectedProviders.FirstOrDefault(provider =>
                provider != null &&
                string.Equals(provider.ProviderKey, resolvedProvider.ProviderKey, StringComparison.OrdinalIgnoreCase));
            if (selectedProvider == null)
            {
                return false;
            }

            return MatchesProviderSelection(
                game,
                resolvedProvider,
                selectedProvider.ProviderKey,
                selectedProvider.ProviderName,
                steamTargetMode,
                knownSourceNames);
        }

        private static bool MatchesProviderSelection(
            Game game,
            IDataProvider resolvedProvider,
            string selectedProviderKey,
            string selectedProviderName,
            SteamRefreshTargetMode steamTargetMode,
            IEnumerable<string> knownSourceNames)
        {
            if (game == null || resolvedProvider == null || string.IsNullOrWhiteSpace(selectedProviderKey))
            {
                return false;
            }

            if (!string.Equals(resolvedProvider.ProviderKey, selectedProviderKey, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (string.Equals(selectedProviderKey, LocalProviderKey, StringComparison.OrdinalIgnoreCase))
            {
                return !SourceMatchesAny(game, BuildExcludedLocalSourceNames(knownSourceNames));
            }

            if (string.Equals(selectedProviderKey, SteamRefreshTargeting.SteamProviderKey, StringComparison.OrdinalIgnoreCase))
            {
                switch (steamTargetMode)
                {
                    case SteamRefreshTargetMode.OwnedOnly:
                        return SourceMatches(game, selectedProviderName, SteamRefreshTargeting.SteamProviderKey);

                    case SteamRefreshTargetMode.FamilySharedOnly:
                        return SourceMatches(game, SteamRefreshTargeting.SteamFamilySharingSourceName);

                    default:
                        return SourceMatches(
                            game,
                            selectedProviderName,
                            SteamRefreshTargeting.SteamProviderKey,
                            SteamRefreshTargeting.SteamFamilySharingSourceName);
                }
            }

            return SourceMatches(game, selectedProviderName, selectedProviderKey);
        }

        private static bool SourceMatches(Game game, params string[] expectedSourceNames)
        {
            var sourceName = game?.Source?.Name?.Trim();
            if (string.IsNullOrWhiteSpace(sourceName))
            {
                return false;
            }

            return expectedSourceNames.Any(expectedSourceName =>
                !string.IsNullOrWhiteSpace(expectedSourceName) &&
                string.Equals(sourceName, expectedSourceName.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        private static bool SourceMatchesAny(Game game, IEnumerable<string> expectedSourceNames)
        {
            return SourceMatches(game, (expectedSourceNames ?? Enumerable.Empty<string>()).ToArray());
        }

        private static IEnumerable<string> BuildExcludedLocalSourceNames(IEnumerable<string> knownSourceNames)
        {
            var configuredNames = (knownSourceNames ?? Enumerable.Empty<string>())
                .Where(sourceName => !string.IsNullOrWhiteSpace(sourceName))
                .Select(sourceName => sourceName.Trim())
                .Where(sourceName => !string.Equals(sourceName, LocalProviderKey, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (configuredNames.Count > 0)
            {
                return configuredNames;
            }

            return DefaultLocalExcludedSelectionKeys
                .SelectMany(GetDefaultSelectionSourceNames)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static IEnumerable<string> GetDefaultSelectionSourceNames(string selectionKey)
        {
            if (string.IsNullOrWhiteSpace(selectionKey) ||
                string.Equals(selectionKey, LocalProviderKey, StringComparison.OrdinalIgnoreCase))
            {
                return Enumerable.Empty<string>();
            }

            if (string.Equals(selectionKey, SteamFamilySharingSelectionKey, StringComparison.OrdinalIgnoreCase))
            {
                return new[] { SteamRefreshTargeting.SteamFamilySharingSourceName };
            }

            var localizedName = ResourceProvider.GetString($"LOCPlayAch_Provider_{selectionKey}");
            if (string.Equals(selectionKey, SteamRefreshTargeting.SteamProviderKey, StringComparison.OrdinalIgnoreCase))
            {
                return new[]
                {
                    SteamRefreshTargeting.SteamProviderKey,
                    localizedName
                }.Where(value => !string.IsNullOrWhiteSpace(value));
            }

            return new[]
            {
                selectionKey,
                localizedName
            }.Where(value => !string.IsNullOrWhiteSpace(value));
        }
    }
}