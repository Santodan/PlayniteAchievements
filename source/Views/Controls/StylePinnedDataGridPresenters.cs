using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace PlayniteAchievements.Views.Controls
{
    /// <summary>
    /// DataGrid hands each cell and column header its column's or the grid's style through a
    /// coercion that only applies while the grid transfers the value. A tree change (a view
    /// leaving or entering the window) re-reads the style without it, so every realized cell and
    /// header takes its implicit style and then the transferred one again, rebuilding its template
    /// both times. These presenters set the transferred style as the container's local value when
    /// they prepare it, which a tree change does not re-read.
    /// </summary>
    internal static class DataGridStylePin
    {
        public static void Apply(FrameworkElement container, Style style)
        {
            if (style == null)
            {
                if (container.ReadLocalValue(FrameworkElement.StyleProperty) != DependencyProperty.UnsetValue)
                {
                    container.ClearValue(FrameworkElement.StyleProperty);
                }
            }
            else if (!ReferenceEquals(container.ReadLocalValue(FrameworkElement.StyleProperty), style))
            {
                container.Style = style;
            }
        }

        // The presenter's own implicit style (a theme may define one for the base type) keeps
        // applying to the derived type through a resource reference keyed by the base type.
        public static void UseBaseTypeStyle(FrameworkElement presenter, System.Type baseType)
        {
            presenter.SetResourceReference(FrameworkElement.StyleProperty, baseType);
        }
    }

    public class StylePinnedDataGridCellsPresenter : DataGridCellsPresenter
    {
        static StylePinnedDataGridCellsPresenter()
        {
            DefaultStyleKeyProperty.OverrideMetadata(
                typeof(StylePinnedDataGridCellsPresenter),
                new FrameworkPropertyMetadata(typeof(DataGridCellsPresenter)));
        }

        public StylePinnedDataGridCellsPresenter()
        {
            DataGridStylePin.UseBaseTypeStyle(this, typeof(DataGridCellsPresenter));
        }

        protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
        {
            base.PrepareContainerForItemOverride(element, item);
            if (element is DataGridCell cell)
            {
                var grid = ItemsControl.ItemsControlFromItemContainer(TemplatedParent) as DataGrid;
                DataGridStylePin.Apply(cell, cell.Column?.CellStyle ?? grid?.CellStyle);

                // A template column's cell content is a presenter of the column's CellTemplate;
                // its template children end their implicit style lookups there.
                if (cell.Content is ContentPresenter generated && !(generated.Resources is Helpers.ImplicitStyleMirror))
                {
                    generated.Resources = new Helpers.ImplicitStyleMirror();
                }
            }
        }
    }

    public class StylePinnedDataGridColumnHeadersPresenter : DataGridColumnHeadersPresenter
    {
        static StylePinnedDataGridColumnHeadersPresenter()
        {
            DefaultStyleKeyProperty.OverrideMetadata(
                typeof(StylePinnedDataGridColumnHeadersPresenter),
                new FrameworkPropertyMetadata(typeof(DataGridColumnHeadersPresenter)));
        }

        public StylePinnedDataGridColumnHeadersPresenter()
        {
            DataGridStylePin.UseBaseTypeStyle(this, typeof(DataGridColumnHeadersPresenter));

            // A header's Column does not notify when it changes, so a recycled header keeps every
            // binding that reads through Column (hidden text, header alignment) on the column it
            // was first made for. Headers are few; each column's header is built for it.
            VirtualizingPanel.SetVirtualizationMode(this, VirtualizationMode.Standard);
        }

        // The presenter sits in the grid's ScrollViewer template.
        private DataGrid OwnerGrid => (TemplatedParent as FrameworkElement)?.TemplatedParent as DataGrid;

        protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
        {
            base.PrepareContainerForItemOverride(element, item);
            if (element is DataGridColumnHeader header)
            {
                DataGridStylePin.Apply(header, header.Column?.HeaderStyle ?? OwnerGrid?.ColumnHeaderStyle);
            }
        }
    }
}
