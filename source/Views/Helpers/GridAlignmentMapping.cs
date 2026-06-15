using System.Windows;
using System.Windows.Controls;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Views.Helpers
{
    public static class GridAlignmentMapping
    {
        public static HorizontalAlignment ToHorizontalAlignment(GridAlignment alignment)
        {
            switch (alignment)
            {
                case GridAlignment.Center:
                    return HorizontalAlignment.Center;
                case GridAlignment.Right:
                    return HorizontalAlignment.Right;
                case GridAlignment.Left:
                default:
                    return HorizontalAlignment.Left;
            }
        }

        public static TextAlignment ToTextAlignment(GridAlignment alignment)
        {
            switch (alignment)
            {
                case GridAlignment.Center:
                    return TextAlignment.Center;
                case GridAlignment.Right:
                    return TextAlignment.Right;
                case GridAlignment.Left:
                default:
                    return TextAlignment.Left;
            }
        }

        public static VerticalAlignment ToVerticalAlignment(GridVerticalAlignment alignment)
        {
            switch (alignment)
            {
                case GridVerticalAlignment.Top:
                    return VerticalAlignment.Top;
                case GridVerticalAlignment.Bottom:
                    return VerticalAlignment.Bottom;
                case GridVerticalAlignment.Center:
                default:
                    return VerticalAlignment.Center;
            }
        }
    }
}
