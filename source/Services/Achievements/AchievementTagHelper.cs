using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Playnite.SDK;
using PlayniteAchievements.Models.Achievements;

namespace PlayniteAchievements.Services.Achievements
{
    /// <summary>
    /// Parsing and naming for achievement tags. An achievement stores its tags as pipe-joined keys;
    /// names and colors come from the tag lists in <see cref="AchievementTagRegistry"/>.
    /// </summary>
    internal static class AchievementTagHelper
    {
        public const string Missable = "Missable";
        public const string Unobtainable = "Unobtainable";
        public const string Softcore = "Softcore";
        public const string Hardcore = "Hardcore";

        public const string UserKeyPrefix = "u-";

        // The tags every install starts with, in display order: play mode, where it sits in a
        // playthrough, what kind of task it is, what the run asks of the player, whether it can
        // still be earned, and a catch-all.
        private static readonly string[] BuiltInKeys =
        {
            "Singleplayer",
            "Multiplayer",
            "Progression",
            "SideQuest",
            "WinCondition",
            "PostGame",
            "Completion",
            "Collectable",
            "Cumulative",
            "Challenge",
            "Difficulty",
            "Stackable",
            Missable,
            Unobtainable,
            "Miscellaneous"
        };

        // Tags that carry behavior keyed to them; they can be renamed and recolored, not deleted.
        private static readonly HashSet<string> SystemKeys =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Missable, Unobtainable };

        // How an achievement was earned (RetroAchievements unlock mode). Only a provider sets these;
        // they are never shown as tags or assigned by hand.
        private static readonly string[] DerivedKeys = { Softcore, Hardcore };

        private static readonly Dictionary<string, string> CanonicalByAlias = BuildAliases();

        private static readonly char[] ValueSeparators = { '|', ',', ';', '/' };

        private const int CacheCapacity = 1024;

        private static readonly ConcurrentDictionary<string, IReadOnlyList<string>> ComponentsCache =
            new ConcurrentDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        public static IReadOnlyList<string> BuiltInTagKeys => BuiltInKeys;

        public static bool IsSystemTag(string key) => !string.IsNullOrWhiteSpace(key) && SystemKeys.Contains(key.Trim());

        public static bool IsDerivedTag(string key) =>
            !string.IsNullOrWhiteSpace(key) && DerivedKeys.Contains(key.Trim(), StringComparer.OrdinalIgnoreCase);

        public static bool IsBuiltInTag(string key) =>
            !string.IsNullOrWhiteSpace(key) && BuiltInKeys.Contains(key.Trim(), StringComparer.OrdinalIgnoreCase);

        public static string NewUserKey() => UserKeyPrefix + Guid.NewGuid().ToString("N").Substring(0, 8);

        /// <summary>The built-in tag list a fresh install starts with.</summary>
        public static List<AchievementTagDefinition> CreateDefaultDefinitions() =>
            BuiltInKeys.Select(key => new AchievementTagDefinition { Key = key }).ToList();

        /// <summary>
        /// A clean copy of a tag list: blank keys dropped, the first definition of a key kept, names
        /// and colors trimmed (a malformed color becomes the default). With
        /// <paramref name="ensureSystemTags"/>, missing system tags are appended, so they can never
        /// be deleted from the global list. Null stays null.
        /// </summary>
        public static List<AchievementTagDefinition> NormalizeDefinitions(
            IEnumerable<AchievementTagDefinition> definitions,
            bool ensureSystemTags)
        {
            if (definitions == null)
            {
                return null;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<AchievementTagDefinition>();
            foreach (var definition in definitions)
            {
                var key = (definition?.Key ?? string.Empty).Trim();
                if (key.Length == 0 || IsDerivedTag(key) || CategoryKindHelper.IsKnownKind(key) ||
                    key.IndexOfAny(ValueSeparators) >= 0 || !seen.Add(key))
                {
                    continue;
                }

                var name = (definition.Name ?? string.Empty).Trim();
                result.Add(new AchievementTagDefinition
                {
                    Key = CanonicalByAlias.TryGetValue(key, out var canonical) ? canonical : key,
                    Name = name.Length == 0 ? null : name,
                    Color = NormalizeColor(definition.Color),
                    Icon = NormalizeIcon(definition.Icon),
                    Hidden = definition.Hidden
                });
            }

            if (ensureSystemTags)
            {
                foreach (var key in BuiltInKeys.Where(IsSystemTag).Where(key => !seen.Contains(key)))
                {
                    result.Add(new AchievementTagDefinition { Key = key });
                }
            }

            return result;
        }

        /// <summary>
        /// An icon codepoint as upper-case hex ("EDD7"), or null when <paramref name="icon"/> is not a
        /// private-use codepoint, which is where icon fonts put their glyphs.
        /// </summary>
        public static string NormalizeIcon(string icon)
        {
            var value = (icon ?? string.Empty).Trim();
            if (value.StartsWith("U+", StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring(2);
            }

            return int.TryParse(value, System.Globalization.NumberStyles.HexNumber, null, out var codepoint) &&
                   codepoint >= 0xE000 && codepoint <= 0xF8FF
                ? codepoint.ToString("X4")
                : null;
        }

        /// <summary>The character for an icon codepoint, or null for none.</summary>
        public static string IconGlyph(string icon)
        {
            var normalized = NormalizeIcon(icon);
            return normalized == null
                ? null
                : ((char)int.Parse(normalized, System.Globalization.NumberStyles.HexNumber)).ToString();
        }

        /// <summary>
        /// "#RRGGBB" in upper case, or null when <paramref name="color"/> is not a color. An alpha
        /// channel ("#AARRGGBB", as the color picker returns) is dropped: tag colors are opaque.
        /// </summary>
        public static string NormalizeColor(string color)
        {
            var value = (color ?? string.Empty).Trim();
            if (value.Length == 9 && value[0] == '#')
            {
                value = "#" + value.Substring(3);
            }
            if (value.Length == 7 && value[0] == '#' &&
                value.Skip(1).All(c => Uri.IsHexDigit(c)))
            {
                return value.ToUpperInvariant();
            }

            return null;
        }

        /// <summary>
        /// The canonical keys of <paramref name="rawValue"/> as a shared list. Cached; callers must
        /// not mutate it.
        /// </summary>
        public static IReadOnlyList<string> GetComponents(string rawValue)
        {
            var key = rawValue ?? string.Empty;
            if (ComponentsCache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            IReadOnlyList<string> result = Parse(rawValue).AsReadOnly();
            if (ComponentsCache.Count < CacheCapacity)
            {
                ComponentsCache.TryAdd(key, result);
            }

            return result;
        }

        /// <summary>
        /// Splits a stored tag value into keys. Built-in aliases canonicalize ("collectible" reads
        /// as Collectable); any other key is kept verbatim. Kind tokens and the old Default
        /// placeholder are not tags and are dropped.
        /// </summary>
        public static List<string> Parse(string rawValue)
        {
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                return new List<string>();
            }

            var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in rawValue.Split(ValueSeparators, StringSplitOptions.RemoveEmptyEntries))
            {
                var token = part.Trim();
                if (token.Length == 0 ||
                    string.Equals(token, AchievementCategoryHelper.DefaultCategoryLabel, StringComparison.OrdinalIgnoreCase) ||
                    CategoryKindHelper.IsKnownKind(token))
                {
                    continue;
                }

                values.Add(CanonicalByAlias.TryGetValue(token, out var canonical) ? canonical : token);
            }

            return Order(values);
        }

        public static string Normalize(string rawValue)
        {
            var values = GetComponents(rawValue);
            return values.Count == 0 ? null : string.Join("|", values);
        }

        public static string Combine(IEnumerable<string> keys) =>
            keys == null ? null : Normalize(string.Join("|", keys));

        public static bool Contains(string value, string key) =>
            !string.IsNullOrWhiteSpace(key) && GetComponents(value).Contains(key.Trim(), StringComparer.OrdinalIgnoreCase);

        public static bool IsMissable(string value) => Contains(value, Missable);

        public static bool IsUnobtainable(string value) => Contains(value, Unobtainable);

        /// <summary>True for an achievement earned in RetroAchievements softcore mode.</summary>
        public static bool IsSoftcore(string value) => Contains(value, Softcore);

        public static bool IsHardcore(string value) => Contains(value, Hardcore);

        /// <summary>
        /// <paramref name="value"/> with <paramref name="key"/> added or removed. Derived keys are
        /// the provider's and are left as they are.
        /// </summary>
        public static string WithTag(string value, string key, bool include)
        {
            var token = Normalize(key);
            if (token == null || IsDerivedTag(token))
            {
                return Normalize(value);
            }

            var values = Parse(value)
                .Where(existing => !string.Equals(existing, token, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (include)
            {
                values.Add(token);
            }

            return Combine(values);
        }

        /// <summary><paramref name="value"/> without its derived keys; null when nothing else remains.</summary>
        public static string StripDerived(string value) =>
            Combine(GetComponents(value).Where(key => !IsDerivedTag(key)));

        /// <summary>The derived keys of <paramref name="value"/> only; null when it has none.</summary>
        public static string DerivedOnly(string value) =>
            Combine(GetComponents(value).Where(IsDerivedTag));

        /// <summary>
        /// The effective tags of an achievement whose provider gives <paramref name="providerValue"/>
        /// and whose user override is <paramref name="overrideValue"/> (null for none). The override
        /// supplies every tag except the derived ones, which always come from the provider: only a
        /// RetroAchievements refresh says how an achievement was earned. An empty, non-null override
        /// clears every assigned tag.
        /// </summary>
        public static string ApplyOverride(string providerValue, string overrideValue)
        {
            if (overrideValue == null)
            {
                return Normalize(providerValue);
            }

            return Combine(GetComponents(overrideValue)
                .Where(key => !IsDerivedTag(key))
                .Concat(GetComponents(providerValue).Where(IsDerivedTag)));
        }

        /// <summary>
        /// The tag override to store for a row, or null when <paramref name="value"/> is what the
        /// provider already gives it. Clearing every tag on a row the provider tags stores "" so the
        /// override still says "no tags".
        /// </summary>
        public static string OverrideOrNull(string value, string providerValue)
        {
            var assigned = StripDerived(value);
            var provider = StripDerived(providerValue);
            if (string.Equals(assigned ?? string.Empty, provider ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return assigned ?? string.Empty;
        }

        /// <summary>
        /// Joined names of the tags in <paramref name="value"/> that resolve for
        /// <paramref name="gameId"/>, in display order; derived and deleted tags are left out, and
        /// hidden ones unless <paramref name="includeHidden"/>.
        /// </summary>
        public static string ToDisplayText(string value, Guid? gameId = null, bool includeHidden = false) =>
            string.Join(", ", AchievementTagRegistry.ResolveVisible(value, gameId, includeHidden).Select(AchievementTagRegistry.GetName));

        /// <summary>The localized default name of a built-in tag, or null for any other key.</summary>
        public static string GetBuiltInName(string key)
        {
            var canonical = Normalize(key);
            if (canonical == null || !(IsBuiltInTag(canonical) || IsDerivedTag(canonical)))
            {
                return null;
            }

            var value = ResourceProvider.GetString($"LOCPlayAch_AchievementTag_{canonical}");
            return string.IsNullOrWhiteSpace(value) || value.StartsWith("<!", StringComparison.Ordinal)
                ? canonical
                : value;
        }

        /// <summary>
        /// The name a tag shows until it is renamed: a built-in tag's localized name, a source's key
        /// spelled out ("PartlyDiscontinued" as "Partly Discontinued", "BuggyPlus" as "Buggy+"), or the key
        /// itself for a user tag.
        /// </summary>
        public static string GetDefaultName(string key)
        {
            var builtIn = GetBuiltInName(key);
            if (builtIn != null)
            {
                return builtIn;
            }

            var trimmed = (key ?? string.Empty).Trim();
            if (trimmed.StartsWith(UserKeyPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return trimmed;
            }

            var name = new System.Text.StringBuilder(trimmed.Length + 4);
            for (var i = 0; i < trimmed.Length; i++)
            {
                var c = trimmed[i];
                if (i > 0 && (char.IsUpper(c) || (char.IsDigit(c) && !char.IsDigit(trimmed[i - 1]))) && name[name.Length - 1] != ' ')
                {
                    name.Append(' ');
                }

                name.Append(c);
            }

            var spelled = name.ToString();
            return spelled.EndsWith(" Plus", StringComparison.Ordinal) ? spelled.Substring(0, spelled.Length - 5) + "+"
                : spelled.EndsWith(" Minus", StringComparison.Ordinal) ? spelled.Substring(0, spelled.Length - 6) + "-"
                : spelled;
        }

        private static List<string> Order(HashSet<string> values)
        {
            var builtIn = BuiltInKeys.Concat(DerivedKeys).Where(values.Contains);
            var other = values
                .Where(value => !IsBuiltInTag(value) && !IsDerivedTag(value))
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase);
            return builtIn.Concat(other).ToList();
        }

        private static Dictionary<string, string> BuildAliases()
        {
            var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in BuiltInKeys.Concat(DerivedKeys))
            {
                aliases[key] = key;
            }

            void Alias(string canonical, params string[] names)
            {
                foreach (var name in names)
                {
                    aliases[name] = canonical;
                }
            }

            Alias("Singleplayer", "single player", "sp");
            Alias("Multiplayer", "multi player", "mp");
            Alias("Progression", "story");
            Alias("SideQuest", "side quest", "side-quest", "side_quest", "sideprogression",
                "side progression", "side-progression", "side_progression");
            Alias("WinCondition", "win_condition", "win condition", "win-condition");
            Alias("PostGame", "post game", "post-game", "post_game");
            Alias("Completion", "completionist");
            Alias("Collectable", "collectible");
            Alias("Cumulative", "grind");
            Alias(Missable, "miss-able");
            Alias("Difficulty", "diff");
            Alias("Miscellaneous", "misc");
            Alias("Stackable", "stack", "stacking");
            Alias(Softcore, "casual");
            return aliases;
        }
    }
}
