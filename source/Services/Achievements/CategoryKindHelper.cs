using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Playnite.SDK;

namespace PlayniteAchievements.Services.Achievements
{
    /// <summary>
    /// The kind of a category: which part of a game's content its achievements belong to. Kinds
    /// combine (Update modifies Base or DLC, as in "DLC|Update") and are stored pipe-joined in
    /// canonical order. Null means the category has no kind.
    /// </summary>
    /// <remarks>
    /// The vocabulary is <see cref="KnownKinds"/>. A token outside it is kept verbatim rather than
    /// dropped, so a kind added by a later build survives a round trip through this one.
    /// </remarks>
    internal static class CategoryKindHelper
    {
        public const string Base = "Base";
        public const string Dlc = "DLC";
        public const string Update = "Update";
        public const string Subset = "Subset";

        /// <summary>
        /// Stored as a category's kind to say it has none, overriding whatever the provider gives
        /// its members. Parses to no kind.
        /// </summary>
        public const string None = "None";

        private static readonly string[] KnownKinds = { Base, Dlc, Update, Subset };

        private static readonly Dictionary<string, string> CanonicalByAlias =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["base"] = Base,
                ["dlc"] = Dlc,
                ["update"] = Update,
                ["subset"] = Subset
            };

        private static readonly char[] ValueSeparators = { '|', ',', ';', '/' };

        private const int CacheCapacity = 512;

        private static readonly ConcurrentDictionary<string, IReadOnlyList<string>> ComponentsCache =
            new ConcurrentDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        /// <summary>The kinds a user can assign, in display order.</summary>
        public static IReadOnlyList<string> AssignableKinds => KnownKinds;

        public static bool IsKnownKind(string token) =>
            !string.IsNullOrWhiteSpace(token) && CanonicalByAlias.ContainsKey(token.Trim());

        /// <summary>
        /// The canonical kinds of <paramref name="rawValue"/> as a shared list. Cached; callers
        /// must not mutate it.
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
                    string.Equals(token, None, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(token, AchievementCategoryHelper.DefaultCategoryLabel, StringComparison.OrdinalIgnoreCase))
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

        /// <summary>
        /// A stored kind value in canonical form: <see cref="None"/> stays None, anything else is
        /// normalized, blank is null.
        /// </summary>
        public static string NormalizeStored(string rawValue)
        {
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                return null;
            }

            return string.Equals(rawValue.Trim(), None, StringComparison.OrdinalIgnoreCase)
                ? None
                : Normalize(rawValue);
        }

        public static string Combine(IEnumerable<string> kinds) =>
            kinds == null ? null : Normalize(string.Join("|", kinds));

        public static string WithKind(string value, string kind, bool include)
        {
            var token = Normalize(kind);
            if (token == null)
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

        /// <summary>
        /// An achievement's effective kind: the kind stored on its category (<see cref="None"/>
        /// meaning none), else the provider's.
        /// </summary>
        public static string ResolveEffective(string storedCategoryKind, string providerKind) =>
            Normalize(!string.IsNullOrWhiteSpace(storedCategoryKind) ? storedCategoryKind : providerKind);

        public static bool Contains(string value, string kind) =>
            GetComponents(value).Contains(kind, StringComparer.OrdinalIgnoreCase);

        public static bool IsBase(string value) => Contains(value, Base);

        public static bool IsDlc(string value) => Contains(value, Dlc);

        public static bool IsUpdate(string value) => Contains(value, Update);

        public static bool IsSubset(string value) => Contains(value, Subset);

        /// <summary>
        /// The kind every one of <paramref name="values"/> shares, or null when they disagree, when
        /// any has no kind, or when there are none. A category has a kind only when all its
        /// achievements agree.
        /// </summary>
        public static string ResolveUnanimous(IEnumerable<string> values)
        {
            if (values == null)
            {
                return null;
            }

            string shared = null;
            var any = false;
            foreach (var value in values)
            {
                var kind = Normalize(value);
                if (kind == null)
                {
                    return null;
                }

                if (!any)
                {
                    shared = kind;
                    any = true;
                }
                else if (!string.Equals(shared, kind, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
            }

            return shared;
        }

        /// <summary>Joined localized names, empty for no kind.</summary>
        public static string ToDisplayText(string value) =>
            string.Join(", ", GetComponents(value).Select(ToKindDisplayText));

        public static string ToKindDisplayText(string kind)
        {
            var canonical = Normalize(kind);
            if (canonical == null)
            {
                return string.Empty;
            }

            if (!IsKnownKind(canonical))
            {
                return canonical;
            }

            var value = ResourceProvider.GetString($"LOCPlayAch_CategoryKind_{canonical}");
            return string.IsNullOrWhiteSpace(value) || value.StartsWith("<!", StringComparison.Ordinal)
                ? canonical
                : value;
        }

        /// <summary>
        /// A key that sorts kinds in canonical order (Base, DLC, Update, Subset, then unknown kinds
        /// by name), a combination after its first kind alone, and no kind last. Letters and digits
        /// only, so ordinal and culture comparisons agree.
        /// </summary>
        public static string ToSortKey(string value)
        {
            var components = GetComponents(value);
            if (components.Count == 0)
            {
                return "z";
            }

            return string.Concat(components.Select(kind =>
            {
                var index = Array.IndexOf(KnownKinds, kind);
                return index >= 0 ? index.ToString() : "9" + kind.ToLowerInvariant();
            }));
        }

        private static List<string> Order(HashSet<string> values)
        {
            var known = KnownKinds.Where(values.Contains);
            var unknown = values
                .Where(value => !CanonicalByAlias.ContainsKey(value))
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase);
            return known.Concat(unknown).ToList();
        }
    }
}
