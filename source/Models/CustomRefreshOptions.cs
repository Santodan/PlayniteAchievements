using System;
using System.Collections.Generic;
using System.Linq;
using PlayniteAchievements.Models.Friends;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Models
{
    [Flags]
    public enum RefreshSubjects
    {
        None = 0,
        CurrentUser = 1,
        Friends = 2,
        All = CurrentUser | Friends
    }

    public enum RefreshGameScope
    {
        All,
        Recent,
        Installed,
        Favorites,
        LibrarySelected,
        Missing,
        Explicit,
        Shared,
        SelectedGame
    }

    /// <summary>
    /// Canonical refresh options shared by current-user and friend refreshes.
    /// </summary>
    public sealed class RefreshOptions
    {
        public RefreshSubjects Subjects { get; set; } = RefreshSubjects.CurrentUser;
        public RefreshGameScope Scope { get; set; } = RefreshGameScope.Recent;
        public IReadOnlyCollection<string> ProviderKeys { get; set; }
        public IReadOnlyCollection<Guid> PlayniteGameIds { get; set; }
        public IReadOnlyCollection<Guid> ExcludeGameIds { get; set; }
        public IReadOnlyCollection<int> ProviderAppIds { get; set; }
        public IReadOnlyCollection<string> ProviderGameKeys { get; set; }
        public IReadOnlyCollection<FriendAccountRef> FriendAccounts { get; set; }
        public IReadOnlyCollection<string> FriendExternalUserIds { get; set; }
        public int? RecentLimitOverride { get; set; }
        public bool? IncludeUnplayedOverride { get; set; }
        public bool RespectUserExclusions { get; set; } = true;
        public bool ForceBypassExclusionsForExplicitIncludes { get; set; } = true;
        public bool ForceIconRefresh { get; set; }
        public bool ForceDefinitionRefresh { get; set; }

        // Reuse cached Ok schemas even for scopes that normally force a definition re-fetch
        // (SelectedGame/Full). Set by latency-sensitive programmatic callers such as the
        // in-game poller; user-initiated refreshes leave this false.
        public bool PreferCachedDefinitions { get; set; }
        public bool? RunProvidersInParallelOverride { get; set; }

        public RefreshOptions Clone()
        {
            return new RefreshOptions
            {
                Subjects = Subjects,
                Scope = Scope,
                ProviderKeys = NormalizeStrings(ProviderKeys),
                PlayniteGameIds = PlayniteGameIds?
                    .Where(id => id != Guid.Empty)
                    .Distinct()
                    .ToList(),
                ExcludeGameIds = ExcludeGameIds?
                    .Where(id => id != Guid.Empty)
                    .Distinct()
                    .ToList(),
                ProviderAppIds = ProviderAppIds?
                    .Where(id => id > 0)
                    .Distinct()
                    .ToList(),
                ProviderGameKeys = NormalizeStrings(ProviderGameKeys),
                FriendAccounts = NormalizeFriendAccounts(FriendAccounts),
                FriendExternalUserIds = NormalizeStrings(FriendExternalUserIds),
                RecentLimitOverride = RecentLimitOverride,
                IncludeUnplayedOverride = IncludeUnplayedOverride,
                RespectUserExclusions = RespectUserExclusions,
                ForceBypassExclusionsForExplicitIncludes = ForceBypassExclusionsForExplicitIncludes,
                ForceIconRefresh = ForceIconRefresh,
                ForceDefinitionRefresh = ForceDefinitionRefresh,
                PreferCachedDefinitions = PreferCachedDefinitions,
                RunProvidersInParallelOverride = RunProvidersInParallelOverride
            };
        }

        public static RefreshOptions FromCustom(CustomRefreshOptions options)
        {
            var source = options?.Clone() ?? new CustomRefreshOptions();
            return new RefreshOptions
            {
                Subjects = RefreshSubjects.CurrentUser,
                Scope = MapCustomScope(source.Scope),
                ProviderKeys = source.ProviderKeys,
                PlayniteGameIds = source.IncludeGameIds,
                ExcludeGameIds = source.ExcludeGameIds,
                RecentLimitOverride = source.RecentLimitOverride,
                IncludeUnplayedOverride = source.IncludeUnplayedOverride,
                RespectUserExclusions = source.RespectUserExclusions,
                ForceBypassExclusionsForExplicitIncludes = source.ForceBypassExclusionsForExplicitIncludes,
                RunProvidersInParallelOverride = source.RunProvidersInParallelOverride
            }.Clone();
        }

        public static RefreshOptions FromFriend(FriendCustomRefreshOptions options)
        {
            var source = options?.Clone() ?? new FriendCustomRefreshOptions();
            return new RefreshOptions
            {
                Subjects = RefreshSubjects.Friends,
                Scope = MapFriendScope(source.Scope),
                ProviderKeys = source.ProviderKeys,
                PlayniteGameIds = source.PlayniteGameIds,
                ProviderAppIds = source.ProviderAppIds,
                ProviderGameKeys = source.ProviderGameKeys,
                FriendAccounts = source.FriendAccounts,
                FriendExternalUserIds = source.FriendExternalUserIds,
                ForceDefinitionRefresh = source.ForceDefinitionRefresh,
                PreferCachedDefinitions = source.PreferCachedDefinitions
            }.Clone();
        }

        public CustomRefreshOptions ToCustomOptions()
        {
            return new CustomRefreshOptions
            {
                ProviderKeys = ProviderKeys,
                Scope = MapRefreshScopeToCustom(Scope),
                IncludeGameIds = PlayniteGameIds,
                ExcludeGameIds = ExcludeGameIds,
                RecentLimitOverride = RecentLimitOverride,
                IncludeUnplayedOverride = IncludeUnplayedOverride,
                RespectUserExclusions = RespectUserExclusions,
                ForceBypassExclusionsForExplicitIncludes = ForceBypassExclusionsForExplicitIncludes,
                RunProvidersInParallelOverride = RunProvidersInParallelOverride
            }.Clone();
        }

        private static List<string> NormalizeStrings(IEnumerable<string> values)
        {
            return values?
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static List<FriendAccountRef> NormalizeFriendAccounts(IEnumerable<FriendAccountRef> accounts)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var normalized = new List<FriendAccountRef>();
            foreach (var account in accounts ?? Enumerable.Empty<FriendAccountRef>())
            {
                var next = account?.Clone()?.Normalize();
                if (string.IsNullOrWhiteSpace(next?.Key) || !seen.Add(next.Key))
                {
                    continue;
                }

                normalized.Add(next);
            }

            return normalized.Count == 0 ? null : normalized;
        }

        private static RefreshGameScope MapCustomScope(CustomGameScope scope)
        {
            switch (scope)
            {
                case CustomGameScope.All: return RefreshGameScope.All;
                case CustomGameScope.Installed: return RefreshGameScope.Installed;
                case CustomGameScope.Favorites: return RefreshGameScope.Favorites;
                case CustomGameScope.Recent: return RefreshGameScope.Recent;
                case CustomGameScope.LibrarySelected: return RefreshGameScope.LibrarySelected;
                case CustomGameScope.Missing: return RefreshGameScope.Missing;
                case CustomGameScope.Explicit: return RefreshGameScope.Explicit;
                default: return RefreshGameScope.All;
            }
        }

        private static CustomGameScope MapRefreshScopeToCustom(RefreshGameScope scope)
        {
            switch (scope)
            {
                case RefreshGameScope.All: return CustomGameScope.All;
                case RefreshGameScope.Installed: return CustomGameScope.Installed;
                case RefreshGameScope.Favorites: return CustomGameScope.Favorites;
                case RefreshGameScope.Recent: return CustomGameScope.Recent;
                case RefreshGameScope.LibrarySelected: return CustomGameScope.LibrarySelected;
                case RefreshGameScope.Missing: return CustomGameScope.Missing;
                case RefreshGameScope.Explicit:
                case RefreshGameScope.SelectedGame:
                    return CustomGameScope.Explicit;
                default:
                    return CustomGameScope.All;
            }
        }

        private static RefreshGameScope MapFriendScope(FriendRefreshScope scope)
        {
            switch (scope)
            {
                case FriendRefreshScope.Full: return RefreshGameScope.All;
                case FriendRefreshScope.Shared: return RefreshGameScope.Shared;
                case FriendRefreshScope.Installed: return RefreshGameScope.Installed;
                case FriendRefreshScope.SelectedGame: return RefreshGameScope.SelectedGame;
                case FriendRefreshScope.Custom: return RefreshGameScope.Explicit;
                case FriendRefreshScope.Recent:
                default:
                    return RefreshGameScope.Recent;
            }
        }
    }

    public enum CustomGameScope
    {
        All,
        Installed,
        Favorites,
        Recent,
        LibrarySelected,
        Missing,
        Explicit
    }

    /// <summary>
    /// Persisted custom refresh preset.
    /// </summary>
    public sealed class CustomRefreshPreset
    {
        public const int MaxPresetCount = 50;
        public const int MaxNameLength = 64;

        public string Name { get; set; }
        public CustomRefreshOptions Options { get; set; }

        public CustomRefreshPreset Clone()
        {
            return new CustomRefreshPreset
            {
                Name = SanitizeName(Name),
                Options = Options?.Clone() ?? new CustomRefreshOptions()
            };
        }

        public static string SanitizeName(string name)
        {
            var trimmed = (name ?? string.Empty).Trim();
            if (trimmed.Length > MaxNameLength)
            {
                trimmed = trimmed.Substring(0, MaxNameLength);
            }

            return trimmed;
        }

        public static IReadOnlyList<CustomRefreshPreset> NormalizePresets(
            IEnumerable<CustomRefreshPreset> presets,
            int maxCount = MaxPresetCount)
        {
            var normalized = new List<CustomRefreshPreset>();
            if (presets == null)
            {
                return normalized;
            }

            var countLimit = Math.Max(0, maxCount);
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var preset in presets)
            {
                if (normalized.Count >= countLimit)
                {
                    break;
                }

                var name = SanitizeName(preset?.Name);
                if (string.IsNullOrWhiteSpace(name) || !seenNames.Add(name))
                {
                    continue;
                }

                normalized.Add(new CustomRefreshPreset
                {
                    Name = name,
                    Options = preset?.Options?.Clone() ?? new CustomRefreshOptions()
                });
            }

            normalized.Sort((a, b) => string.Compare(a?.Name, b?.Name, StringComparison.OrdinalIgnoreCase));
            return normalized;
        }

        public static CustomRefreshOptions PruneUnavailableSelections(
            CustomRefreshOptions options,
            IEnumerable<string> availableProviderKeys,
            IEnumerable<Guid> availableGameIds,
            out int removedProviderCount,
            out int removedGameCount)
        {
            var resolved = options?.Clone() ?? new CustomRefreshOptions();

            var availableProviders = new HashSet<string>(
                availableProviderKeys?
                    .Where(key => !string.IsNullOrWhiteSpace(key))
                    .Select(key => key.Trim()) ??
                Enumerable.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
            var requestedProviders = resolved.ProviderKeys?
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Select(key => key.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>();
            var validProviders = requestedProviders
                .Where(key => availableProviders.Contains(key))
                .ToList();
            removedProviderCount = requestedProviders.Count - validProviders.Count;
            resolved.ProviderKeys = validProviders;

            var availableGames = new HashSet<Guid>(
                availableGameIds?
                    .Where(gameId => gameId != Guid.Empty) ??
                Enumerable.Empty<Guid>());
            var includeIds = resolved.IncludeGameIds?
                .Where(gameId => gameId != Guid.Empty)
                .Distinct()
                .ToList() ?? new List<Guid>();
            var excludeIds = resolved.ExcludeGameIds?
                .Where(gameId => gameId != Guid.Empty)
                .Distinct()
                .ToList() ?? new List<Guid>();
            var validIncludes = includeIds.Where(availableGames.Contains).ToList();
            var validExcludes = excludeIds.Where(availableGames.Contains).ToList();
            removedGameCount = (includeIds.Count - validIncludes.Count) + (excludeIds.Count - validExcludes.Count);
            resolved.IncludeGameIds = validIncludes;
            resolved.ExcludeGameIds = validExcludes;

            return resolved;
        }
    }

    /// <summary>
    /// Ad-hoc options for custom refresh execution.
    /// </summary>
    public sealed class CustomRefreshOptions
    {
        public IReadOnlyCollection<string> ProviderKeys { get; set; }
        public CustomGameScope Scope { get; set; } = CustomGameScope.All;
        public IReadOnlyCollection<Guid> IncludeGameIds { get; set; }
        public IReadOnlyCollection<Guid> ExcludeGameIds { get; set; }
        public int? RecentLimitOverride { get; set; }
        public bool? IncludeUnplayedOverride { get; set; }
        public bool RespectUserExclusions { get; set; } = true;
        public bool ForceBypassExclusionsForExplicitIncludes { get; set; } = true;
        public bool? RunProvidersInParallelOverride { get; set; }

        public CustomRefreshOptions Clone()
        {
            return new CustomRefreshOptions
            {
                ProviderKeys = ProviderKeys?
                    .Where(key => !string.IsNullOrWhiteSpace(key))
                    .Select(key => key.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                Scope = Scope,
                IncludeGameIds = IncludeGameIds?
                    .Where(id => id != Guid.Empty)
                    .Distinct()
                    .ToList(),
                ExcludeGameIds = ExcludeGameIds?
                    .Where(id => id != Guid.Empty)
                    .Distinct()
                    .ToList(),
                RecentLimitOverride = RecentLimitOverride,
                IncludeUnplayedOverride = IncludeUnplayedOverride,
                RespectUserExclusions = RespectUserExclusions,
                ForceBypassExclusionsForExplicitIncludes = ForceBypassExclusionsForExplicitIncludes,
                RunProvidersInParallelOverride = RunProvidersInParallelOverride
            };
        }
    }
}
