using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Models
{
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

    public enum SteamRefreshTargetMode
    {
        All,
        OwnedOnly,
        FamilySharedOnly
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
        public SteamRefreshTargetMode SteamTargetMode { get; set; } = SteamRefreshTargetMode.All;
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
                SteamTargetMode = SteamTargetMode,
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
