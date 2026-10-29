using Newtonsoft.Json;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Services.Achievements;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.GameCustomData
{
    /// <summary>
    /// Moves per-achievement custom data between games whose API names carry a local scope
    /// (<see cref="IAchievementKeyScope"/>), such as a Minecraft game spanning several instances.
    /// Stored data stays keyed by full API names. A package carries portable keys instead:
    /// <see cref="ToPortable"/> writes them on export and <see cref="ToGame"/> spreads them over
    /// the target game's own scopes on import. Category paths rooted at a scope's name travel
    /// with that root replaced by <see cref="ScopeToken"/>. Both are no-ops for an unscoped
    /// provider.
    /// </summary>
    internal static class GameCustomDataPortableScoping
    {
        /// <summary>Stands for "the instance this achievement is in" at the root of a portable category path.</summary>
        public const string ScopeToken = "{instance}";

        // ---- export ------------------------------------------------------------------------

        /// <summary>
        /// Rewrites <paramref name="portable"/> for export. An entry set the same way on every
        /// scope of the game that has the achievement becomes one portable entry; entries that
        /// differ between scopes stay scoped. Category paths rooted at a scope's name are rooted at
        /// <see cref="ScopeToken"/>. <paramref name="iconIdentity"/> maps an icon value to what
        /// counts as the same image (for example a content hash); null compares the values.
        /// </summary>
        public static void ToPortable(
            GameCustomDataPortableFile portable,
            IAchievementKeyScope scope,
            IEnumerable<AchievementDetail> rows,
            Func<string, string> iconIdentity = null)
        {
            if (portable == null || scope == null)
            {
                return;
            }

            var game = GameScopes.Build(scope, rows);
            var icon = iconIdentity ?? (value => value);

            // Category paths first, so two scopes filing an achievement under their own
            // "<instance> > Story" compare as the same value.
            if (portable.AchievementOverrides != null)
            {
                foreach (var pair in portable.AchievementOverrides.Where(pair => pair.Value != null))
                {
                    pair.Value.Category = TokenizeForKey(pair.Value.Category, pair.Key, game);
                }
            }

            portable.AchievementOverrides = CollapseMap(
                portable.AchievementOverrides,
                game,
                (left, right) => OverrideIdentity(left, icon) == OverrideIdentity(right, icon));
            ProjectMirrors(portable);

            portable.FilteredAchievementApiNames = CollapseList(portable.FilteredAchievementApiNames, game);
            portable.SummaryFilteredAchievementApiNames = CollapseList(portable.SummaryFilteredAchievementApiNames, game);
            portable.GoalAchievementApiNames = CollapseList(portable.GoalAchievementApiNames, game);
            if (portable.Capstones != null)
            {
                portable.Capstones = CollapseList(portable.Capstones.Where(item => item != null).Select(item => item.ApiName), game)
                    ?.Select(apiName => new CapstoneAssignment { ApiName = apiName })
                    .ToList();
            }

            portable.ManualCapstoneApiName = PortableKey(portable.ManualCapstoneApiName, game) ?? portable.ManualCapstoneApiName;
            portable.AchievementOrder = Distinct(portable.AchievementOrder?.Select(apiName => PortableKey(apiName, game) ?? apiName));

            portable.AchievementCategoryOrder = DistinctLabels(portable.AchievementCategoryOrder?.Select(path => TokenizeAnyScope(path, game)));
            portable.AchievementCategoryImageOverrides = CollapseCategoryImages(portable.AchievementCategoryImageOverrides, game, icon);
            portable.AchievementCategoryKinds = CollapseCategoryKinds(portable.AchievementCategoryKinds, game);
            if (portable.GameSummaryCategory != null)
            {
                portable.GameSummaryCategory.Label = TokenizeAnyScope(portable.GameSummaryCategory.Label, game);
                portable.GameSummaryCategory.ProviderLabel = TokenizeAnyScope(portable.GameSummaryCategory.ProviderLabel, game);
            }
        }

        // ---- import ------------------------------------------------------------------------

        /// <summary>
        /// Rewrites <paramref name="portable"/> onto the target game's API names. An entry for one
        /// of the game's own scopes stays where it is and wins; a portable entry goes to that
        /// achievement in every scope of the game; an entry for a scope the game does not have is
        /// read as portable, the first one winning when several disagree. Keys that match nothing
        /// are kept as they are. A no-op when the game's rows carry no scope.
        /// </summary>
        public static void ToGame(
            GameCustomDataPortableFile portable,
            IAchievementKeyScope scope,
            IEnumerable<AchievementDetail> rows)
        {
            if (portable == null || scope == null)
            {
                return;
            }

            var game = GameScopes.Build(scope, rows);
            if (game.ScopeOrder.Count == 0)
            {
                return;
            }

            var foreign = CollectForeignScopes(portable, game);

            portable.AchievementOverrides = ExpandMap(
                portable.AchievementOverrides,
                game,
                (target, value) =>
                {
                    var copy = value?.Clone();
                    if (copy != null)
                    {
                        copy.Category = ResolveForKey(copy.Category, target, game, foreign);
                    }

                    return copy;
                });
            portable.AchievementCategoryOverrides = ExpandMap(
                portable.AchievementCategoryOverrides,
                game,
                (target, value) => ResolveForKey(value, target, game, foreign));
            portable.AchievementCategoryTypeOverrides = ExpandMap(portable.AchievementCategoryTypeOverrides, game, (target, value) => value);
            portable.AchievementNotes = ExpandMap(portable.AchievementNotes, game, (target, value) => value);
            portable.AchievementUnlockedIconOverrides = ExpandMap(portable.AchievementUnlockedIconOverrides, game, (target, value) => value);
            portable.AchievementLockedIconOverrides = ExpandMap(portable.AchievementLockedIconOverrides, game, (target, value) => value);

            portable.FilteredAchievementApiNames = ExpandList(portable.FilteredAchievementApiNames, game);
            portable.SummaryFilteredAchievementApiNames = ExpandList(portable.SummaryFilteredAchievementApiNames, game);
            portable.GoalAchievementApiNames = ExpandList(portable.GoalAchievementApiNames, game);
            if (portable.Capstones != null)
            {
                portable.Capstones = ExpandList(portable.Capstones.Where(item => item != null).Select(item => item.ApiName).ToList(), game)
                    ?.Select(apiName => new CapstoneAssignment { ApiName = apiName })
                    .ToList();
            }

            if (!string.IsNullOrWhiteSpace(portable.ManualCapstoneApiName))
            {
                portable.ManualCapstoneApiName = Targets(portable.ManualCapstoneApiName.Trim(), game)
                    .Select(target => target.ApiName)
                    .FirstOrDefault() ?? portable.ManualCapstoneApiName;
            }

            portable.AchievementOrder = ExpandOrder(portable.AchievementOrder, game);

            portable.AchievementCategoryOrder = DistinctLabels(portable.AchievementCategoryOrder?
                .SelectMany(path => ResolveForEveryScope(path, game, foreign)));
            portable.AchievementCategoryImageOverrides = ExpandCategoryImages(portable.AchievementCategoryImageOverrides, game, foreign);
            portable.AchievementCategoryKinds = ExpandCategoryKinds(portable.AchievementCategoryKinds, game, foreign);
            if (portable.GameSummaryCategory != null)
            {
                portable.GameSummaryCategory.Label = ResolveForFirstScope(portable.GameSummaryCategory.Label, game, foreign);
                portable.GameSummaryCategory.ProviderLabel = ResolveForFirstScope(portable.GameSummaryCategory.ProviderLabel, game, foreign);
            }
        }

        // ---- the game's scopes -------------------------------------------------------------

        /// <summary>
        /// A game's scopes as its rows show them: each scope in row order, its name (the root of
        /// the rows' provider category), and every API name per portable key.
        /// </summary>
        internal sealed class GameScopes
        {
            private GameScopes(IAchievementKeyScope scope)
            {
                Scope = scope;
            }

            public IAchievementKeyScope Scope { get; }

            public List<string> ScopeOrder { get; } = new List<string>();

            public HashSet<string> Scopes { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public Dictionary<string, string> NameByScope { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            public HashSet<string> Names { get; } = new HashSet<string>(CategoryPathHelper.LabelComparer);

            public HashSet<string> ApiNames { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public Dictionary<string, List<string>> ApiNamesByPortableKey { get; } =
                new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            public static GameScopes Build(IAchievementKeyScope scope, IEnumerable<AchievementDetail> rows)
            {
                var game = new GameScopes(scope);
                foreach (var row in rows ?? Enumerable.Empty<AchievementDetail>())
                {
                    var apiName = row?.ApiName?.Trim();
                    if (string.IsNullOrEmpty(apiName) || !game.ApiNames.Add(apiName))
                    {
                        continue;
                    }

                    if (!scope.TrySplit(apiName, out var rowScope, out var portableKey))
                    {
                        continue;
                    }

                    if (game.Scopes.Add(rowScope))
                    {
                        game.ScopeOrder.Add(rowScope);
                    }

                    if (!game.ApiNamesByPortableKey.TryGetValue(portableKey, out var apiNames))
                    {
                        apiNames = new List<string>();
                        game.ApiNamesByPortableKey[portableKey] = apiNames;
                    }

                    apiNames.Add(apiName);

                    var category = string.IsNullOrWhiteSpace(row.ProviderCategory) ? row.Category : row.ProviderCategory;
                    if (!game.NameByScope.ContainsKey(rowScope) && !string.IsNullOrWhiteSpace(category))
                    {
                        var root = CategoryPathHelper.Split(category)[0];
                        if (!string.Equals(root, AchievementCategoryHelper.DefaultCategoryLabel, StringComparison.OrdinalIgnoreCase))
                        {
                            game.NameByScope[rowScope] = root;
                            game.Names.Add(root);
                        }
                    }
                }

                return game;
            }

            public string ScopeOf(string apiName)
            {
                return Scope.TrySplit(apiName, out var scope, out _) ? scope : null;
            }

            public string NameOf(string scope)
            {
                return scope != null && NameByScope.TryGetValue(scope, out var name) ? name : null;
            }
        }

        // ---- export helpers ----------------------------------------------------------------

        /// <summary>The portable key of a scoped API name, or null for an unscoped one.</summary>
        private static string PortableKey(string apiName, GameScopes game)
        {
            return game.Scope.TrySplit(apiName, out _, out var portableKey) ? portableKey : null;
        }

        /// <summary>
        /// Portable keys whose entries may collapse: every scoped key of the group is present,
        /// for every scope of the game that has the achievement, and no unscoped entry already
        /// uses the portable key.
        /// </summary>
        private static Dictionary<string, List<string>> GroupCollapsible(IEnumerable<string> keys, GameScopes game)
        {
            var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var groups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in keys)
            {
                all.Add(key);
                var portableKey = PortableKey(key, game);
                if (portableKey == null)
                {
                    continue;
                }

                if (!groups.TryGetValue(portableKey, out var group))
                {
                    group = new List<string>();
                    groups[portableKey] = group;
                }

                group.Add(key);
            }

            foreach (var portableKey in groups.Keys.ToList())
            {
                var present = new HashSet<string>(groups[portableKey], StringComparer.OrdinalIgnoreCase);
                var required = game.ApiNamesByPortableKey.TryGetValue(portableKey, out var local)
                    ? (IReadOnlyCollection<string>)local
                    : present;
                if (all.Contains(portableKey) || !required.All(present.Contains))
                {
                    groups.Remove(portableKey);
                }
            }

            return groups;
        }

        private static Dictionary<string, T> CollapseMap<T>(
            Dictionary<string, T> map,
            GameScopes game,
            Func<T, T, bool> same)
        {
            if (map == null || map.Count == 0)
            {
                return map;
            }

            var entries = map
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Key))
                .Select(pair => new KeyValuePair<string, T>(pair.Key.Trim(), pair.Value))
                .ToList();
            var groups = GroupCollapsible(entries.Select(pair => pair.Key), game);
            var byKey = entries
                .GroupBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Value, StringComparer.OrdinalIgnoreCase);
            foreach (var portableKey in groups.Keys.ToList())
            {
                var values = groups[portableKey].Select(key => byKey[key]).ToList();
                if (!values.Skip(1).All(value => same(values[0], value)))
                {
                    groups.Remove(portableKey);
                }
            }

            var result = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in entries)
            {
                var portableKey = PortableKey(pair.Key, game);
                var key = portableKey != null && groups.ContainsKey(portableKey) ? portableKey : pair.Key;
                if (!result.ContainsKey(key))
                {
                    result[key] = pair.Value;
                }
            }

            return result;
        }

        private static List<string> CollapseList(IEnumerable<string> apiNames, GameScopes game)
        {
            if (apiNames == null)
            {
                return null;
            }

            var keys = apiNames
                .Where(apiName => !string.IsNullOrWhiteSpace(apiName))
                .Select(apiName => apiName.Trim())
                .ToList();
            var groups = GroupCollapsible(keys, game);
            return Distinct(keys.Select(key =>
            {
                var portableKey = PortableKey(key, game);
                return portableKey != null && groups.ContainsKey(portableKey) ? portableKey : key;
            }));
        }

        private static Dictionary<string, CategoryImageOverrideData> CollapseCategoryImages(
            Dictionary<string, CategoryImageOverrideData> map,
            GameScopes game,
            Func<string, string> icon)
        {
            if (map == null || map.Count == 0)
            {
                return map;
            }

            var groups = map
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Value != null)
                .GroupBy(pair => TokenizeAnyScope(pair.Key, game), CategoryPathHelper.LabelComparer);
            var result = new Dictionary<string, CategoryImageOverrideData>(CategoryPathHelper.LabelComparer);
            foreach (var group in groups)
            {
                var entries = group.ToList();
                var collapses = !string.Equals(group.Key, entries[0].Key, CategoryPathHelper.LabelComparison) &&
                                ScopedPathCount(group.Key, map.Keys, game) == game.Names.Count &&
                                entries.All(pair => icon(pair.Value.Art) == icon(entries[0].Value.Art));
                if (collapses)
                {
                    result[group.Key] = entries[0].Value;
                    continue;
                }

                foreach (var pair in entries)
                {
                    result[pair.Key] = pair.Value;
                }
            }

            return result;
        }

        /// <summary>
        /// Category kinds keyed by path, collapsed like category art: one portable entry when every
        /// scope's copy of the path has the same kind.
        /// </summary>
        private static Dictionary<string, string> CollapseCategoryKinds(Dictionary<string, string> map, GameScopes game)
        {
            if (map == null || map.Count == 0)
            {
                return map;
            }

            var result = new Dictionary<string, string>(CategoryPathHelper.LabelComparer);
            foreach (var group in map
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
                .GroupBy(pair => TokenizeAnyScope(pair.Key, game), CategoryPathHelper.LabelComparer))
            {
                var entries = group.ToList();
                var collapses = !string.Equals(group.Key, entries[0].Key, CategoryPathHelper.LabelComparison) &&
                                ScopedPathCount(group.Key, map.Keys, game) == game.Names.Count &&
                                entries.All(pair => string.Equals(pair.Value, entries[0].Value, StringComparison.OrdinalIgnoreCase));
                if (collapses)
                {
                    result[group.Key] = entries[0].Value;
                    continue;
                }

                foreach (var pair in entries)
                {
                    result[pair.Key] = pair.Value;
                }
            }

            return result;
        }

        private static Dictionary<string, string> ExpandCategoryKinds(
            Dictionary<string, string> map,
            GameScopes game,
            ISet<string> foreign)
        {
            if (map == null || map.Count == 0)
            {
                return map;
            }

            var result = new Dictionary<string, string>(CategoryPathHelper.LabelComparer);
            foreach (var pair in map.Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value)))
            {
                foreach (var path in ResolveForEveryScope(pair.Key, game, foreign))
                {
                    if (!result.ContainsKey(path))
                    {
                        result[path] = pair.Value;
                    }
                }
            }

            return result;
        }

        /// <summary>How many of the game's scope names root a path that tokenizes to <paramref name="tokenized"/>.</summary>
        private static int ScopedPathCount(string tokenized, IEnumerable<string> paths, GameScopes game)
        {
            return paths
                .Where(path => string.Equals(TokenizeAnyScope(path, game), tokenized, CategoryPathHelper.LabelComparison))
                .Select(path => CategoryPathHelper.Split(path)[0])
                .Distinct(CategoryPathHelper.LabelComparer)
                .Count();
        }

        /// <summary>What decides whether two scopes' overrides are the same: their JSON, with icons by identity.</summary>
        private static string OverrideIdentity(AchievementOverride value, Func<string, string> icon)
        {
            if (value == null)
            {
                return null;
            }

            var copy = value.Clone();
            copy.UnlockedIconPath = string.IsNullOrWhiteSpace(copy.UnlockedIconPath) ? null : icon(copy.UnlockedIconPath);
            copy.LockedIconPath = string.IsNullOrWhiteSpace(copy.LockedIconPath) ? null : icon(copy.LockedIconPath);
            return JsonConvert.SerializeObject(copy);
        }

        /// <summary>
        /// Rebuilds the schema-7 mirror maps from <see cref="GameCustomDataPortableFile.AchievementOverrides"/>,
        /// so the record and its mirrors carry the same keys after a collapse.
        /// </summary>
        private static void ProjectMirrors(GameCustomDataPortableFile portable)
        {
            Dictionary<string, string> Project(Func<AchievementOverride, string> field)
            {
                var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var pair in portable.AchievementOverrides ?? new Dictionary<string, AchievementOverride>())
                {
                    var value = pair.Value == null ? null : field(pair.Value);
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        result[pair.Key] = value;
                    }
                }

                return result.Count > 0 ? result : null;
            }

            portable.AchievementCategoryOverrides = Project(value => value.Category);
            // Tags and kinds live on the record and the kind map now; the old type map is not written.
            portable.AchievementCategoryTypeOverrides = null;
            portable.AchievementNotes = Project(value => value.Note);
            portable.AchievementUnlockedIconOverrides = Project(value => value.UnlockedIconPath);
            portable.AchievementLockedIconOverrides = Project(value => value.LockedIconPath);
        }

        private static string TokenizeForKey(string path, string apiName, GameScopes game)
        {
            return Tokenize(path, game.NameOf(game.ScopeOf(apiName)));
        }

        private static string TokenizeAnyScope(string path, GameScopes game)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return path;
            }

            var root = CategoryPathHelper.Split(path)[0];
            return game.Names.Contains(root) ? Tokenize(path, root) : path;
        }

        private static string Tokenize(string path, string scopeName)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(scopeName))
            {
                return path;
            }

            var segments = CategoryPathHelper.Split(path);
            if (!string.Equals(segments[0], scopeName, CategoryPathHelper.LabelComparison))
            {
                return path;
            }

            return string.Join(CategoryPathHelper.Separator, new[] { ScopeToken }.Concat(segments.Skip(1)));
        }

        // ---- import helpers ----------------------------------------------------------------

        private enum Precedence
        {
            ForeignScope = 1,
            Portable = 2,
            Exact = 3
        }

        private struct Target
        {
            public Target(string apiName, Precedence precedence)
            {
                ApiName = apiName;
                Precedence = precedence;
            }

            public string ApiName { get; }

            public Precedence Precedence { get; }
        }

        /// <summary>Where one package key lands in the game, and how strongly.</summary>
        private static IEnumerable<Target> Targets(string key, GameScopes game)
        {
            if (game.ApiNames.Contains(key))
            {
                return new[] { new Target(key, Precedence.Exact) };
            }

            string portableKey;
            Precedence precedence;
            if (game.Scope.TrySplit(key, out var scope, out var split))
            {
                if (game.Scopes.Contains(scope))
                {
                    // One of the game's own scopes without that row: kept as written.
                    return new[] { new Target(key, Precedence.Exact) };
                }

                portableKey = split;
                precedence = Precedence.ForeignScope;
            }
            else
            {
                portableKey = key;
                precedence = Precedence.Portable;
            }

            return game.ApiNamesByPortableKey.TryGetValue(portableKey, out var apiNames)
                ? apiNames.Select(apiName => new Target(apiName, precedence))
                : new[] { new Target(key, Precedence.Exact) };
        }

        private static Dictionary<string, T> ExpandMap<T>(
            Dictionary<string, T> map,
            GameScopes game,
            Func<string, T, T> project)
        {
            if (map == null || map.Count == 0)
            {
                return map;
            }

            var result = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
            var precedences = new Dictionary<string, Precedence>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in map)
            {
                if (string.IsNullOrWhiteSpace(pair.Key))
                {
                    continue;
                }

                foreach (var target in Targets(pair.Key.Trim(), game))
                {
                    if (precedences.TryGetValue(target.ApiName, out var held) && held >= target.Precedence)
                    {
                        continue;
                    }

                    precedences[target.ApiName] = target.Precedence;
                    result[target.ApiName] = project(target.ApiName, pair.Value);
                }
            }

            return result;
        }

        private static List<string> ExpandList(IEnumerable<string> apiNames, GameScopes game)
        {
            return apiNames == null
                ? null
                : Distinct(apiNames
                    .Where(apiName => !string.IsNullOrWhiteSpace(apiName))
                    .SelectMany(apiName => Targets(apiName.Trim(), game).Select(target => target.ApiName)));
        }

        /// <summary>
        /// The package order laid out once per scope, in the game's scope order, so each
        /// instance's rows follow the package's sequence; keys that land outside any scope follow.
        /// </summary>
        private static List<string> ExpandOrder(List<string> order, GameScopes game)
        {
            if (order == null || order.Count == 0)
            {
                return order;
            }

            var targets = order
                .Where(apiName => !string.IsNullOrWhiteSpace(apiName))
                .Select(apiName => Targets(apiName.Trim(), game).Select(target => target.ApiName).ToList())
                .ToList();
            var result = new List<string>();
            foreach (var scope in game.ScopeOrder)
            {
                foreach (var apiNames in targets)
                {
                    result.AddRange(apiNames.Where(apiName => string.Equals(game.ScopeOf(apiName), scope, StringComparison.OrdinalIgnoreCase)));
                }
            }

            result.AddRange(targets.SelectMany(apiNames => apiNames).Where(apiName => game.ScopeOf(apiName) == null));
            return Distinct(result);
        }

        private static Dictionary<string, CategoryImageOverrideData> ExpandCategoryImages(
            Dictionary<string, CategoryImageOverrideData> map,
            GameScopes game,
            ISet<string> foreign)
        {
            if (map == null || map.Count == 0)
            {
                return map;
            }

            var result = new Dictionary<string, CategoryImageOverrideData>(CategoryPathHelper.LabelComparer);
            foreach (var pair in map.Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Value != null))
            {
                foreach (var path in ResolveForEveryScope(pair.Key, game, foreign))
                {
                    if (!result.ContainsKey(path))
                    {
                        result[path] = pair.Value.Clone();
                    }
                }
            }

            return result;
        }

        /// <summary>The scopes named in the package's keys that the game does not have.</summary>
        private static HashSet<string> CollectForeignScopes(GameCustomDataPortableFile portable, GameScopes game)
        {
            var keys = (portable.AchievementOverrides?.Keys ?? Enumerable.Empty<string>())
                .Concat(portable.AchievementCategoryOverrides?.Keys ?? Enumerable.Empty<string>())
                .Concat(portable.AchievementCategoryTypeOverrides?.Keys ?? Enumerable.Empty<string>())
                .Concat(portable.AchievementNotes?.Keys ?? Enumerable.Empty<string>())
                .Concat(portable.AchievementUnlockedIconOverrides?.Keys ?? Enumerable.Empty<string>())
                .Concat(portable.AchievementLockedIconOverrides?.Keys ?? Enumerable.Empty<string>())
                .Concat(portable.AchievementOrder ?? Enumerable.Empty<string>())
                .Concat(portable.FilteredAchievementApiNames ?? Enumerable.Empty<string>())
                .Concat(portable.SummaryFilteredAchievementApiNames ?? Enumerable.Empty<string>())
                .Concat(portable.GoalAchievementApiNames ?? Enumerable.Empty<string>())
                .Concat(portable.Capstones?.Select(item => item?.ApiName) ?? Enumerable.Empty<string>());
            var foreign = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in keys)
            {
                if (game.Scope.TrySplit(key, out var scope, out _) && !game.Scopes.Contains(scope))
                {
                    foreign.Add(scope);
                }
            }

            return foreign;
        }

        /// <summary>
        /// The segments after the root when <paramref name="path"/> is rooted at the token or at
        /// a foreign scope (its key standing in for its name, which a package does not carry);
        /// null for any other path.
        /// </summary>
        private static IReadOnlyList<string> ScopedTail(string path, GameScopes game, ISet<string> foreign)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var segments = CategoryPathHelper.Split(path);
            var root = segments[0];
            var scoped = string.Equals(root, ScopeToken, StringComparison.Ordinal) ||
                         (foreign.Contains(root) && !game.Names.Contains(root));
            return scoped ? segments.Skip(1).ToList() : null;
        }

        private static string Root(string scopeName, IReadOnlyList<string> tail)
        {
            var segments = string.IsNullOrWhiteSpace(scopeName)
                ? tail
                : new[] { scopeName }.Concat(tail).ToList();
            return segments.Count == 0
                ? AchievementCategoryHelper.DefaultCategoryLabel
                : CategoryPathHelper.Join(segments);
        }

        private static string ResolveForKey(string path, string apiName, GameScopes game, ISet<string> foreign)
        {
            var tail = ScopedTail(path, game, foreign);
            return tail == null ? path : Root(game.NameOf(game.ScopeOf(apiName)), tail);
        }

        private static IEnumerable<string> ResolveForEveryScope(string path, GameScopes game, ISet<string> foreign)
        {
            var tail = ScopedTail(path, game, foreign);
            if (tail == null)
            {
                return new[] { path };
            }

            var names = game.ScopeOrder.Select(game.NameOf).Where(name => name != null).Distinct(CategoryPathHelper.LabelComparer).ToList();
            return names.Count == 0
                ? new[] { Root(null, tail) }
                : names.Select(name => Root(name, tail));
        }

        private static string ResolveForFirstScope(string path, GameScopes game, ISet<string> foreign)
        {
            return ResolveForEveryScope(path, game, foreign).FirstOrDefault() ?? path;
        }

        // ---- shared ------------------------------------------------------------------------

        /// <summary>API names, deduplicated the way API names compare.</summary>
        private static List<string> Distinct(IEnumerable<string> values)
        {
            return Distinct(values, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Category paths, deduplicated the way category labels compare.</summary>
        private static List<string> DistinctLabels(IEnumerable<string> values)
        {
            return Distinct(values, CategoryPathHelper.LabelComparer);
        }

        private static List<string> Distinct(IEnumerable<string> values, IEqualityComparer<string> comparer)
        {
            if (values == null)
            {
                return null;
            }

            var seen = new HashSet<string>(comparer);
            var result = new List<string>();
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value) && seen.Add(value.Trim()))
                {
                    result.Add(value.Trim());
                }
            }

            return result;
        }
    }
}
