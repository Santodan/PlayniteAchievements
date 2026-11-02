using System.Windows;
using System.Windows.Media;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.ViewModels.Showcase.Widgets
{
    /// <summary>
    /// Backs the Image widget: one stored picture across the whole card, fitted or filled, and
    /// placed by the widget's alignment (where a fitted picture sits, or which part of a filled
    /// one stays in view).
    /// </summary>
    public sealed class ImageWidgetViewModel : ShowcaseWidgetViewModelBase
    {
        private string _imagePath;
        private bool _hasImage;
        private Stretch _stretch = Stretch.UniformToFill;
        private HorizontalAlignment _horizontalAlignment = HorizontalAlignment.Center;
        private VerticalAlignment _verticalAlignment = VerticalAlignment.Center;
        private int _decodePixel = 640;

        public string ImagePath { get => _imagePath; private set => SetValue(ref _imagePath, value); }

        public bool HasImage { get => _hasImage; private set => SetValue(ref _hasImage, value); }

        public Stretch Stretch { get => _stretch; private set => SetValue(ref _stretch, value); }

        public HorizontalAlignment HorizontalAlignment
        {
            get => _horizontalAlignment;
            private set => SetValue(ref _horizontalAlignment, value);
        }

        public VerticalAlignment VerticalAlignment
        {
            get => _verticalAlignment;
            private set => SetValue(ref _verticalAlignment, value);
        }

        public int DecodePixel { get => _decodePixel; private set => SetValue(ref _decodePixel, value); }

        protected override void Refresh()
        {
            var instance = Projection?.Instance;
            var path = ShowcaseWidgetOptions.GetImagePath(instance);
            ImagePath = path;
            HasImage = path != null;
            Stretch = ShowcaseWidgetOptions.GetImageFitMode(instance) == ShowcaseImageFitMode.Fill
                ? Stretch.UniformToFill
                : Stretch.Uniform;
            HorizontalAlignment = ToHorizontal(ShowcaseWidgetOptions.GetHorizontalAlignment(instance));
            VerticalAlignment = ToVertical(ShowcaseWidgetOptions.GetVerticalAlignment(instance));
            DecodePixel = Density == WidgetViewportDensity.Expanded ? 1280 : 640;
        }

        internal static HorizontalAlignment ToHorizontal(ShowcaseHorizontalAlignment value)
        {
            switch (value)
            {
                case ShowcaseHorizontalAlignment.Left:
                    return HorizontalAlignment.Left;
                case ShowcaseHorizontalAlignment.Right:
                    return HorizontalAlignment.Right;
                default:
                    return HorizontalAlignment.Center;
            }
        }

        internal static TextAlignment ToTextAlignment(ShowcaseHorizontalAlignment value)
        {
            switch (value)
            {
                case ShowcaseHorizontalAlignment.Left:
                    return TextAlignment.Left;
                case ShowcaseHorizontalAlignment.Right:
                    return TextAlignment.Right;
                default:
                    return TextAlignment.Center;
            }
        }

        internal static VerticalAlignment ToVertical(ShowcaseVerticalAlignment value)
        {
            switch (value)
            {
                case ShowcaseVerticalAlignment.Top:
                    return VerticalAlignment.Top;
                case ShowcaseVerticalAlignment.Bottom:
                    return VerticalAlignment.Bottom;
                default:
                    return VerticalAlignment.Center;
            }
        }
    }
}
