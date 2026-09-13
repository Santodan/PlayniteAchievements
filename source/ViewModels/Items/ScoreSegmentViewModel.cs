using System.Windows.Media;
using PlayniteAchievements.Common;

namespace PlayniteAchievements.ViewModels.Items
{
    /// <summary>
    /// One level of the current rank, drawn as a single cell of the score card's segmented bar.
    /// The card owns the brushes: a completed level gets the rank accent, an unreached level the
    /// track tint, and the level in progress a hard-stop gradient between the two.
    /// </summary>
    public sealed class ScoreSegmentViewModel : ObservableObject
    {
        private Brush _fill;

        public Brush Fill
        {
            get => _fill;
            set => SetValue(ref _fill, value);
        }
    }
}
