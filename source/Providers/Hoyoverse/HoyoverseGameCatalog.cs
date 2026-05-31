using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace PlayniteAchievements.Providers.Hoyoverse
{
    internal enum HoyoverseGameKind
    {
        None = 0,
        GenshinImpact = 1,
        HonkaiStarRail = 2,
        ZenlessZoneZero = 3
    }

    internal static class HoyoverseGameCatalog
    {
        private sealed class Entry
        {
            public HoyoverseGameKind Kind { get; set; }
            public int AppId { get; set; }
            public string CanonicalName { get; set; }
            public IReadOnlyList<string> Aliases { get; set; }
        }

        private static readonly Entry[] Entries =
        {
            new Entry
            {
                Kind = HoyoverseGameKind.GenshinImpact,
                AppId = 100001,
                CanonicalName = "Genshin Impact",
                Aliases = new[] { "Genshin Impact" }
            },
            new Entry
            {
                Kind = HoyoverseGameKind.HonkaiStarRail,
                AppId = 100002,
                CanonicalName = "Honkai: Star Rail",
                Aliases = new[] { "Honkai: Star Rail", "Honkai Star Rail" }
            },
            new Entry
            {
                Kind = HoyoverseGameKind.ZenlessZoneZero,
                AppId = 100003,
                CanonicalName = "Zenless Zone Zero",
                Aliases = new[] { "Zenless Zone Zero", "ZZZ" }
            }
        };

        private static readonly Dictionary<string, Entry> EntriesByAlias = Entries
            .SelectMany(entry => entry.Aliases.Select(alias => new { Alias = NormalizeName(alias), Entry = entry }))
            .GroupBy(item => item.Alias, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Entry, StringComparer.OrdinalIgnoreCase);

        public static bool TryResolve(Game game, HoyoverseSettings settings, out HoyoverseGameKind kind)
        {
            kind = HoyoverseGameKind.None;
            if (game == null || settings?.IsEnabled != true)
            {
                return false;
            }

            if (!TryResolveByName(game.Name, out kind))
            {
                return false;
            }

            return IsEnabled(kind, settings);
        }

        public static bool TryResolveByName(string gameName, out HoyoverseGameKind kind)
        {
            kind = HoyoverseGameKind.None;
            if (string.IsNullOrWhiteSpace(gameName))
            {
                return false;
            }

            if (!EntriesByAlias.TryGetValue(NormalizeName(gameName), out var entry))
            {
                return false;
            }

            kind = entry.Kind;
            return true;
        }

        public static int GetAppId(HoyoverseGameKind kind)
        {
            return Entries.FirstOrDefault(entry => entry.Kind == kind)?.AppId ?? 0;
        }

        public static string GetCanonicalName(HoyoverseGameKind kind)
        {
            return Entries.FirstOrDefault(entry => entry.Kind == kind)?.CanonicalName ?? "HoYoverse";
        }

        public static string GetExportPath(HoyoverseGameKind kind, HoyoverseSettings settings)
        {
            if (settings == null)
            {
                return null;
            }

            switch (kind)
            {
                case HoyoverseGameKind.GenshinImpact:
                    return settings.GenshinExportPath;
                case HoyoverseGameKind.HonkaiStarRail:
                    return settings.HonkaiStarRailExportPath;
                case HoyoverseGameKind.ZenlessZoneZero:
                    return settings.ZenlessZoneZeroExportPath;
                default:
                    return null;
            }
        }

        private static bool IsEnabled(HoyoverseGameKind kind, HoyoverseSettings settings)
        {
            switch (kind)
            {
                case HoyoverseGameKind.GenshinImpact:
                    return settings.EnableGenshinImpact;
                case HoyoverseGameKind.HonkaiStarRail:
                    return settings.EnableHonkaiStarRail;
                case HoyoverseGameKind.ZenlessZoneZero:
                    return settings.EnableZenlessZoneZero;
                default:
                    return false;
            }
        }

        private static string NormalizeName(string value)
        {
            var trimmed = (value ?? string.Empty).Trim();
            trimmed = Regex.Replace(trimmed, @"[\s:]+", " ");
            return trimmed.ToUpperInvariant();
        }
    }
}
