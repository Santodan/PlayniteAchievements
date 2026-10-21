using System;
using System.Collections.Generic;
using System.Linq;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.ViewModels.Showcase.Widgets
{
    /// <summary>
    /// The Pie widget's control bar: platform and progress (Complete / In Progress) filters over
    /// the games the pie's totals are counted from, and on a Tag pie the tags it draws.
    /// </summary>
    public sealed class PieControlBarAdapter : GameSummaryGridControlBarAdapter
    {
        private readonly HashSet<string> _tagSlices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private Func<IReadOnlyList<string>> _tagOptions = () => Array.Empty<string>();
        private Func<string, string> _tagName = key => key;

        public PieControlBarAdapter()
            : base(chartFilters: true)
        {
            TagSliceItem = new GridMultiSelectFilter(
                this,
                nameof(SelectedTagSliceText),
                () => SelectedTagSliceText,
                () => _tagOptions(),
                IsTagSlice,
                SetTagSlice,
                key => _tagName(key))
            {
                Id = ShowcaseControlBarItems.Tag,
                LabelKey = "LOCTagsLabel",
                // Shown on a Tag pie only, whose tags follow the games in view.
                IsVisible = false,
                AutoHideWhenUnavailable = false,
                Width = 150
            };
            ControlBar.Items.Add(TagSliceItem);
        }

        /// <summary>
        /// Which tags a Tag pie draws. Not a filter: every game still counts toward the slices.
        /// </summary>
        public GridMultiSelectFilter TagSliceItem { get; }

        /// <summary>The picked tags; empty draws every offered tag.</summary>
        public IReadOnlyCollection<string> TagSlices => _tagSlices;

        public string SelectedTagSliceText => _tagSlices.Count == 0
            ? ResourceProvider.GetString("LOCTagsLabel")
            : string.Join(", ", _tagSlices.Select(key => _tagName(key)).OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase));

        /// <summary>
        /// Points the tag picker at the tags the pie offers now and how to name them, and shows
        /// it; null for a pie of another mode hides it.
        /// </summary>
        public void SetTagSliceSource(Func<IReadOnlyList<string>> options, Func<string, string> name)
        {
            _tagOptions = options ?? (() => Array.Empty<string>());
            _tagName = name ?? (key => key);
            TagSliceItem.IsVisible = options != null;
            OnPropertyChanged(nameof(SelectedTagSliceText));
        }

        public override ControlBarFilterState CaptureState()
        {
            var state = base.CaptureState();
            state.Tags = _tagSlices.Count > 0 ? _tagSlices.ToList() : null;
            return state;
        }

        public override void RestoreState(ControlBarFilterState state)
        {
            base.RestoreState(state);
            _tagSlices.Clear();
            foreach (var key in state?.Tags ?? new List<string>())
            {
                if (!string.IsNullOrWhiteSpace(key))
                {
                    _tagSlices.Add(key.Trim());
                }
            }

            OnPropertyChanged(nameof(SelectedTagSliceText));
        }

        private bool IsTagSlice(string key) => key != null && _tagSlices.Contains(key);

        private void SetTagSlice(string key, bool selected)
        {
            if (string.IsNullOrWhiteSpace(key) ||
                !(selected ? _tagSlices.Add(key.Trim()) : _tagSlices.Remove(key.Trim())))
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedTagSliceText));
            RaiseFilterChanged();
        }
    }
}
