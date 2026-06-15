using System;

namespace PlayniteAchievements.Models.Settings
{
    public sealed class WindowPlacementState
    {
        public double Left { get; set; }

        public double Top { get; set; }

        public double Width { get; set; }

        public double Height { get; set; }

        public bool IsMaximized { get; set; }

        public WindowPlacementState Clone()
        {
            return new WindowPlacementState
            {
                Left = Left,
                Top = Top,
                Width = Width,
                Height = Height,
                IsMaximized = IsMaximized
            };
        }

        public bool IsValid()
        {
            return IsFinite(Left) &&
                   IsFinite(Top) &&
                   IsFinite(Width) &&
                   IsFinite(Height) &&
                   Width > 0 &&
                   Height > 0;
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
