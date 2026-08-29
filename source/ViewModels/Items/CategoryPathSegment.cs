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

        /// <summary>The level being shown. Rendered inert: it is where the user already is.</summary>
        public bool IsCurrent { get; }

        public bool IsNavigable { get; }

        /// <summary>The separator trails every hop except the last.</summary>
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
        /// Builds the hops for a drilled path: the level directly above the one being shown, and
        /// that level itself. Anything above them collapses into a single inert marker, so a header
        /// reads "Game &gt; ... &gt; Winter &gt; Frost".
        ///
        /// The header has little room, the game name already sits to the left, and every level is
        /// reachable from the category list anyway - so spelling the whole chain out costs width
        /// without buying navigation.
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
            var first = Math.Max(0, last - 1);
            var result = new List<CategoryPathSegment>(3);

            if (first > 0)
            {
                result.Add(new CategoryPathSegment(
                    Ellipsis,
                    depth: 0,
                    isCurrent: false,
                    isNavigable: false,
                    navigate: null,
                    toolTip: string.Join(
                        " > ",
                        pathSegments.Take(first).Select(CategoryPathHelper.ToDisplayLeaf))));
            }

            for (var index = first; index <= last; index++)
            {
                result.Add(new CategoryPathSegment(
                    CategoryPathHelper.ToDisplayLeaf(pathSegments[index]),
                    index + 1,
                    isCurrent: index == last,
                    isNavigable: index != last,
                    navigate,
                    toolTip: null));
            }

            return result;
        }
    }
}
