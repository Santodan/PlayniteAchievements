using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using PlayniteAchievements.Services.Achievements;
using RelayCommand = PlayniteAchievements.Common.RelayCommand;

namespace PlayniteAchievements.ViewModels.Items
{
    /// <summary>
    /// One hop in the category path a host shows above its achievement grid, so a nested category
    /// reads as "Game &gt; DLC &gt; ... &gt; Winter &gt; Frost".
    ///
    /// <see cref="Depth"/> is how many segments of the drilled path this hop stands for, so 1 is the
    /// outermost category. Navigating returns to the category list scrolled to that ancestor rather
    /// than drilling into it: the list is the map, and landing beside that ancestor's own children
    /// is what makes moving sideways cheap.
    /// </summary>
    public sealed class CategoryPathSegment
    {
        private const string Ellipsis = "…";

        private readonly Action<int> _navigate;
        private ICommand _navigateCommand;

        private CategoryPathSegment(string content, int depth, bool isCurrent, bool isNavigable, Action<int> navigate, string toolTip)
        {
            Content = content;
            Depth = depth;
            IsCurrent = isCurrent;
            IsNavigable = isNavigable;
            ToolTip = toolTip;
            _navigate = navigate;
        }

        public string Content { get; }

        public int Depth { get; }

        /// <summary>Last hop in the path; the separator trails every hop except this one.</summary>
        public bool IsCurrent { get; }

        public bool IsNavigable { get; }

        public bool ShowSeparator => !IsCurrent;

        public string ToolTip { get; }

        /// <summary>
        /// Command form, so the shared template needs no code-behind in any of the hosts that
        /// render a path.
        ///
        /// Deliberately always executable: this RelayCommand never raises CanExecuteChanged
        /// through CommandManager, so a CanExecute predicate is evaluated once and can leave the
        /// button dead. <see cref="Invoke"/> guards instead, and the template disables the button
        /// from <see cref="IsNavigable"/> for the affordance.
        /// </summary>
        public ICommand NavigateCommand => _navigateCommand ??
            (_navigateCommand = new RelayCommand(_ => Invoke()));

        public void Invoke()
        {
            if (IsNavigable)
            {
                _navigate?.Invoke(Depth);
            }
        }

        /// <summary>
        /// Builds the hops for a drilled path: the level being viewed, preceded by an inert marker
        /// when there are levels above it, so a header reads "Game &gt; ... &gt; Frost".
        ///
        /// Every hop is inert: the game-name hop the hosts render to the left of these segments is
        /// the one control that returns to the category list, so the segments only name where the
        /// grid currently is. The marker says the path runs deeper than the name shown.
        ///
        /// Empty when nothing is drilled, so a host can bind an ItemsControl straight to it.
        /// </summary>
        internal static IReadOnlyList<CategoryPathSegment> Build(
            IReadOnlyList<string> pathSegments,
            Action<int> navigate)
        {
            if (pathSegments == null || pathSegments.Count == 0)
            {
                return Array.Empty<CategoryPathSegment>();
            }

            var last = pathSegments.Count - 1;
            var result = new List<CategoryPathSegment>(2);

            if (last > 0)
            {
                result.Add(new CategoryPathSegment(
                    Ellipsis,
                    depth: 0,
                    isCurrent: false,
                    isNavigable: false,
                    navigate: null,
                    toolTip: string.Join(
                        " > ",
                        pathSegments.Take(last).Select(CategoryPathHelper.ToDisplayLeaf))));
            }

            result.Add(new CategoryPathSegment(
                CategoryPathHelper.ToDisplayLeaf(pathSegments[last]),
                last + 1,
                isCurrent: true,
                isNavigable: false,
                navigate: null,
                toolTip: null));

            return result;
        }
    }
}
