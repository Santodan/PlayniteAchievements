using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace PlayniteAchievements.Views.Controls
{
    /// <summary>
    /// Hosts an overlay element inside a <see cref="Grid"/> cell without letting it take part
    /// in the grid's measure. A grid raises a track's minimum size to its largest child, so a
    /// fixed-size handle placed in a track thinner than itself (a 34px merge chevron in a 30px
    /// row) makes the grid ask for more than its slot, and WPF then clips the grid to its
    /// bounds, hiding everything drawn outside them. The host reports a zero desired size, is
    /// arranged to the whole cell, and lets the child align and overhang inside it as it would
    /// have directly in the grid. The grid attached properties and ZIndex are mirrored from the
    /// child, so callers keep positioning the element itself.
    /// </summary>
    public sealed class OverlayLayoutHost : Decorator
    {
        private static readonly DependencyProperty[] MirroredProperties =
        {
            Grid.RowProperty,
            Grid.ColumnProperty,
            Grid.RowSpanProperty,
            Grid.ColumnSpanProperty,
            Panel.ZIndexProperty
        };

        public static OverlayLayoutHost Wrap(FrameworkElement element)
        {
            var host = new OverlayLayoutHost { Child = element };
            foreach (var property in MirroredProperties)
            {
                BindingOperations.SetBinding(host, property, new Binding
                {
                    Source = element,
                    Path = new PropertyPath("(0)", property),
                    Mode = BindingMode.OneWay
                });
            }

            return host;
        }

        /// <summary>The element to remove from the grid: the host when the element is wrapped.</summary>
        public static UIElement HostOf(UIElement element)
        {
            return element is FrameworkElement framework && framework.Parent is OverlayLayoutHost host
                ? host
                : element;
        }

        protected override Size MeasureOverride(Size constraint)
        {
            Child?.Measure(constraint);
            return new Size(0, 0);
        }

        protected override Size ArrangeOverride(Size arrangeSize)
        {
            Child?.Arrange(new Rect(arrangeSize));
            return arrangeSize;
        }
    }
}
