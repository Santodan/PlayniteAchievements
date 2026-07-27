using System;
using System.Windows;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>
    /// Builds the plugin's trophy branding icon as a WPF <see cref="Path"/> for host surfaces
    /// (sidebar item, top-panel item) that take a UIElement icon rather than a resource key.
    /// The trophy geometry comes from LogoResources.xaml; the fill is bound to the inherited
    /// foreground so the icon tracks Playnite's hover/selection colors like the prior font glyph.
    /// </summary>
    internal static class BrandIconFactory
    {
        private const string LogoResourcesUri =
            "pack://application:,,,/PlayniteAchievements;component/Resources/LogoResources.xaml";
        private const string TrophyGeometryKey = "PlayAch.Geometry.TrophyIcon";

        private static Geometry _trophyGeometry;

        public static FrameworkElement CreateTrophyIcon(double size)
        {
            var path = new Path
            {
                Data = GetTrophyGeometry(),
                Stretch = Stretch.Uniform,
                Width = size,
                Height = size
            };

            path.SetBinding(Shape.FillProperty, new Binding
            {
                RelativeSource = new RelativeSource(RelativeSourceMode.Self),
                Path = new PropertyPath(TextElement.ForegroundProperty)
            });

            return path;
        }

        private static Geometry GetTrophyGeometry()
        {
            if (_trophyGeometry != null)
            {
                return _trophyGeometry;
            }

            var dictionary = new ResourceDictionary { Source = new Uri(LogoResourcesUri, UriKind.Absolute) };
            _trophyGeometry = dictionary[TrophyGeometryKey] as Geometry;
            return _trophyGeometry;
        }
    }
}
