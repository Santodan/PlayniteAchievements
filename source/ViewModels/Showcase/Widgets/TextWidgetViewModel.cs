using System.Windows;
using System.Windows.Media;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.ViewModels.Showcase.Widgets
{
    /// <summary>
    /// Backs the Text widget: the widget's text in inline markdown, placed by its alignment, at
    /// one of the theme's font sizes and in the theme font unless the widget names another.
    /// </summary>
    public sealed class TextWidgetViewModel : ShowcaseWidgetViewModelBase
    {
        private string _text;
        private bool _hasText;
        private HorizontalAlignment _horizontalAlignment = HorizontalAlignment.Center;
        private VerticalAlignment _verticalAlignment = VerticalAlignment.Center;
        private TextAlignment _textAlignment = TextAlignment.Center;
        private ShowcaseTextSize _textSize = ShowcaseTextSize.Normal;
        private double _pixelSize = 24;
        private FontFamily _fontFamily;
        private bool _hasFontFamily;

        public string Text { get => _text; private set => SetValue(ref _text, value); }

        public bool HasText { get => _hasText; private set => SetValue(ref _hasText, value); }

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

        /// <summary>Line alignment within the block, matching its horizontal placement.</summary>
        public TextAlignment TextAlignment
        {
            get => _textAlignment;
            private set => SetValue(ref _textAlignment, value);
        }

        /// <summary>Read by the template, which maps each size onto a theme font size resource.</summary>
        public ShowcaseTextSize TextSize { get => _textSize; private set => SetValue(ref _textSize, value); }

        /// <summary>The size in pixels the template applies while <see cref="TextSize"/> is Custom.</summary>
        public double PixelSize { get => _pixelSize; private set => SetValue(ref _pixelSize, value); }

        /// <summary>The chosen font; only applied while <see cref="HasFontFamily"/>, so the theme font is inherited otherwise.</summary>
        public FontFamily FontFamily { get => _fontFamily; private set => SetValue(ref _fontFamily, value); }

        public bool HasFontFamily { get => _hasFontFamily; private set => SetValue(ref _hasFontFamily, value); }

        protected override void Refresh()
        {
            var instance = Projection?.Instance;
            var text = ShowcaseWidgetOptions.GetText(instance);
            Text = text;
            HasText = text != null;

            var horizontal = ShowcaseWidgetOptions.GetHorizontalAlignment(instance);
            HorizontalAlignment = ImageWidgetViewModel.ToHorizontal(horizontal);
            VerticalAlignment = ImageWidgetViewModel.ToVertical(ShowcaseWidgetOptions.GetVerticalAlignment(instance));
            TextAlignment = ImageWidgetViewModel.ToTextAlignment(horizontal);
            TextSize = ShowcaseWidgetOptions.GetTextSize(instance);
            PixelSize = ShowcaseWidgetOptions.GetTextPixelSize(instance);

            var family = ShowcaseWidgetOptions.GetFontFamily(instance);
            if (family != null && !string.Equals(family, FontFamily?.Source, System.StringComparison.Ordinal))
            {
                FontFamily = new FontFamily(family);
            }

            HasFontFamily = family != null;
        }
    }
}
