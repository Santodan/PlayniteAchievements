using System;
using System.Collections.Generic;
using System.Linq;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.ViewModels
{
    /// <summary>
    /// A control bar's Tags filter. It offers the global tags with no game picked, a picked game's
    /// tags (global, its own, and the implicit ones it carries), or the tags every picked game
    /// has. An achievement matches when it has any picked tag; a game, when any of its
    /// achievements, locked or not, does.
    /// </summary>
    public sealed class TagControlBarFilter : PlayniteAchievements.Common.ObservableObject
    {
        private readonly Action _changed;
        private readonly Func<IReadOnlyCollection<Guid>> _pickedGames;
        private readonly Func<Guid, IEnumerable<string>> _presentKeys;
        private readonly HashSet<string> _tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <param name="changed">Raised when the picked tags change.</param>
        /// <param name="pickedGames">The bar's picked games, which scope the offered tags.</param>
        /// <param name="presentKeys">The tag keys a game's achievements carry.</param>
        public TagControlBarFilter(
            Action changed,
            Func<IReadOnlyCollection<Guid>> pickedGames,
            Func<Guid, IEnumerable<string>> presentKeys)
        {
            _changed = changed;
            _pickedGames = pickedGames;
            _presentKeys = presentKeys;
            Item = new GridMultiSelectFilter(
                this,
                nameof(SelectedTagText),
                () => SelectedTagText,
                () => TagOptions,
                IsTagSelected,
                SetTagSelected,
                GetTagName)
            {
                Id = ShowcaseControlBarItems.Tag,
                LabelKey = "LOCTagsLabel",
                AutoHideWhenUnavailable = false,
                Width = 150,
                IconKeyOf = key => IconKey(key, AchievementTagScope.NamingGame(_pickedGames?.Invoke() ?? Array.Empty<Guid>()))
            };
        }

        public GridMultiSelectFilter Item { get; }

        public bool HasSelection => _tags.Count > 0;

        public IReadOnlyCollection<string> SelectedTags => _tags;

        public IReadOnlyList<string> TagOptions =>
            AchievementTagScope.OptionKeys(
                _pickedGames?.Invoke() ?? Array.Empty<Guid>(),
                gameId => _presentKeys?.Invoke(gameId) ?? Enumerable.Empty<string>());

        public string SelectedTagText
        {
            get
            {
                var names = _tags.Select(GetTagName).OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase).ToList();
                return names.Count == 0 ? ResourceProvider.GetString("LOCTagsLabel") : string.Join(", ", names);
            }
        }

        /// <summary>True for an achievement with any picked tag; every achievement while none is picked.</summary>
        public bool MatchesRow(AchievementDisplayItem row) =>
            _tags.Count == 0 ||
            (row != null && _tags.Any(tag => AchievementTagHelper.Contains(row.Tags, tag)));

        /// <summary>True for a game with any achievement, locked or not, carrying a picked tag.</summary>
        public bool MatchesGame(GameSummaryItem game) =>
            _tags.Count == 0 ||
            (game?.TagTotals != null &&
             game.TagTotals.Any(tally => _tags.Any(tag => AchievementTagHelper.Contains(tally.Key, tag))));

        /// <summary>True for a tag-set tally carrying any picked tag; every tally while none is picked.</summary>
        public bool MatchesTally(AchievementTagTally tally) =>
            _tags.Count == 0 ||
            _tags.Any(tag => AchievementTagHelper.Contains(tally.Key, tag));

        public void Capture(ControlBarFilterState state)
        {
            state.Tags = _tags.Count > 0 ? _tags.ToList() : null;
        }

        public void Restore(ControlBarFilterState state)
        {
            _tags.Clear();
            foreach (var tag in state?.Tags ?? new List<string>())
            {
                if (!string.IsNullOrWhiteSpace(tag))
                {
                    _tags.Add(tag.Trim());
                }
            }

            OnPropertyChanged(nameof(SelectedTagText));
        }

        /// <summary>
        /// A tag's badge key: its icon font codepoint in its color, as the Tag pie draws it
        /// ("TagIcon:<codepoint>|<color>", the prefix being ProviderIconConverter.TagIconPrefix,
        /// with an empty color drawing in the text color); null for a tag without an icon.
        /// </summary>
        public static string IconKey(string key, Guid? gameId)
        {
            var definition = AchievementTagRegistry.Resolve(key, gameId);
            var icon = AchievementTagHelper.NormalizeIcon(definition?.Icon);
            return icon == null
                ? null
                : "TagIcon:" + icon + "|" + (AchievementTagHelper.NormalizeColor(definition?.Color) ?? string.Empty);
        }

        /// <summary>The tags' names, which the picked games resolve when exactly one is picked.</summary>
        public void RefreshNames() => OnPropertyChanged(nameof(SelectedTagText));

        // A per-game tag resolves its name only on its game, so a picked game names them.
        private string GetTagName(string key) =>
            AchievementTagRegistry.GetName(key, AchievementTagScope.NamingGame(_pickedGames?.Invoke() ?? Array.Empty<Guid>()));

        private bool IsTagSelected(string key) => key != null && _tags.Contains(key);

        private void SetTagSelected(string key, bool selected)
        {
            if (string.IsNullOrWhiteSpace(key) || !(selected ? _tags.Add(key.Trim()) : _tags.Remove(key.Trim())))
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedTagText));
            _changed?.Invoke();
        }
    }
}
