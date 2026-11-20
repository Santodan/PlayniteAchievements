using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Shapes;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>
    /// The application's implicit styles for the element types grid templates are built from,
    /// held where a template child's own implicit style lookup ends.
    /// </summary>
    /// <remarks>
    /// Every tree change (Playnite moving the sidebar view in or out of its window) has each
    /// element look its implicit style up again. An element that is not a Control and sits in a
    /// template looks through its template up to the templated parent and then through every
    /// dictionary merged into the application, which in Playnite is the theme plus each
    /// extension's dictionaries, several hundred. Nearly all of those lookups find nothing. Merged
    /// at a template's root, this dictionary answers them with what the application lookup would
    /// return: the same style object where the application has one, and an empty style of the
    /// type where it has none, which sets nothing. Controls are left out, since their lookup also
    /// runs through the view's own scoped styles. Only template children reach a template root's
    /// resources, so content placed in the templated parent keeps its own lookup.
    /// </remarks>
    public sealed class ImplicitStyleMirror : ResourceDictionary
    {
        [ThreadStatic]
        private static ResourceDictionary _shared;

        private static readonly Type[] FrameworkTypes =
        {
            typeof(Border), typeof(Decorator), typeof(Grid), typeof(StackPanel), typeof(DockPanel),
            typeof(Canvas), typeof(WrapPanel), typeof(UniformGrid), typeof(VirtualizingStackPanel),
            typeof(SelectiveScrollingGrid), typeof(Viewbox), typeof(ContentPresenter), typeof(ItemsPresenter),
            typeof(ScrollContentPresenter), typeof(DataGridCellsPanel), typeof(DataGridDetailsPresenter),
            typeof(TextBlock), typeof(AccessText), typeof(Image), typeof(Rectangle), typeof(Ellipse),
            typeof(Path), typeof(Line), typeof(Polygon), typeof(Polyline)
        };

        public ImplicitStyleMirror()
        {
            MergedDictionaries.Add(_shared ?? (_shared = Build()));
        }

        private static ResourceDictionary Build()
        {
            var dictionary = new ResourceDictionary();

            // The application's styles belong to its thread; a template built on another thread
            // keeps the ordinary lookup.
            if (Application.Current?.Dispatcher?.CheckAccess() != true)
            {
                return dictionary;
            }

            var application = Application.Current.Resources;
            Type[] assemblyTypes;
            try
            {
                assemblyTypes = typeof(ImplicitStyleMirror).Assembly.GetTypes();
            }
            catch (System.Reflection.ReflectionTypeLoadException ex)
            {
                assemblyTypes = ex.Types.Where(type => type != null).ToArray();
            }

            var ownTypes = assemblyTypes.Where(type =>
                typeof(FrameworkElement).IsAssignableFrom(type) &&
                !typeof(Control).IsAssignableFrom(type) &&
                !type.IsAbstract &&
                type.IsPublic);
            foreach (var type in FrameworkTypes.Concat(ownTypes))
            {
                var style = application[type] as Style;
                if (style == null)
                {
                    style = new Style(type);
                    style.Seal();
                }

                dictionary[type] = style;
            }

            return dictionary;
        }
    }
}
