using PlayniteAchievements.Common;

namespace PlayniteAchievements.Models
{
    /// <summary>
    /// Position model for rendering icons at calculated positions around a pie chart.
    /// </summary>
    public class PieIconPosition : ObservableObject
    {
        private string label;
        private string iconKey;
        private int iconRefreshKey;
        private string colorHex;
        private int count;
        private double x;
        private double y;
        private double offsetX;
        private double offsetY;

        public string Label
        {
            get => label;
            set => SetValue(ref label, value);
        }

        public string IconKey
        {
            get => iconKey;
            set => SetValue(ref iconKey, value);
        }

        public int IconRefreshKey
        {
            get => iconRefreshKey;
            set => SetValue(ref iconRefreshKey, value);
        }

        public string ColorHex
        {
            get => colorHex;
            set => SetValue(ref colorHex, value);
        }

        public int Count
        {
            get => count;
            set => SetValue(ref count, value);
        }

        public double X
        {
            get => x;
            set => SetValue(ref x, value);
        }

        public double Y
        {
            get => y;
            set => SetValue(ref y, value);
        }

        public double OffsetX
        {
            get => offsetX;
            set => SetValue(ref offsetX, value);
        }

        public double OffsetY
        {
            get => offsetY;
            set => SetValue(ref offsetY, value);
        }
    }
}
