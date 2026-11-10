using System.Windows;
using System.Windows.Controls;

namespace PlayniteAchievements.Views.Settings.Controls
{
    /// <summary>
    /// One headed group of option rows in the grid display settings and the showcase widget
    /// settings: Content, Appearance, Layout, Sorting or Control Bar.
    /// </summary>
    /// <remarks>
    /// Its children are the header, then <see cref="Leading"/> (rows a host adds in front of the
    /// group's own, such as a widget's rows before the grid display rows), then the rows declared
    /// in XAML. The header shows only while at least one row is visible, so a group whose rows
    /// do not apply to a surface takes no space.
    /// </remarks>
    public sealed class OptionGroup : StackPanel
    {
        public static readonly DependencyProperty HeaderProperty =
            DependencyProperty.Register(
                nameof(Header),
                typeof(string),
                typeof(OptionGroup),
                new PropertyMetadata(null, (d, e) => ((OptionGroup)d)._header.Text = e.NewValue as string));

        private readonly TextBlock _header;

        public OptionGroup()
        {
            _header = new TextBlock
            {
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Visibility = Visibility.Collapsed
            };
            _header.SetResourceReference(TextBlock.FontSizeProperty, "PlayAch.FontSize.OptionGroupHeader");
            _header.SetResourceReference(TextBlock.ForegroundProperty, "PlayAch.Brush.Text");
            _header.SetResourceReference(MarginProperty, "PlayAch.Thickness.OptionGroupHeading");
            Children.Add(_header);

            Leading = new StackPanel();
            Children.Add(Leading);
        }

        public string Header
        {
            get => (string)GetValue(HeaderProperty);
            set => SetValue(HeaderProperty, value);
        }

        /// <summary>Rows a host puts before the group's own rows.</summary>
        public StackPanel Leading { get; }

        protected override Size MeasureOverride(Size constraint)
        {
            var hasRows = false;
            foreach (UIElement child in Children)
            {
                if (!ReferenceEquals(child, _header) && HasVisibleRow(child))
                {
                    hasRows = true;
                    break;
                }
            }

            var visibility = hasRows && !string.IsNullOrEmpty(Header) ? Visibility.Visible : Visibility.Collapsed;
            if (_header.Visibility != visibility)
            {
                _header.Visibility = visibility;
            }

            return base.MeasureOverride(constraint);
        }

        // A panel counts only through its visible children, so a kind panel whose rows are all
        // collapsed for this surface does not show the header.
        private static bool HasVisibleRow(UIElement element)
        {
            if (element == null || element.Visibility == Visibility.Collapsed)
            {
                return false;
            }

            if (!(element is Panel panel))
            {
                return true;
            }

            foreach (UIElement child in panel.Children)
            {
                if (HasVisibleRow(child))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
