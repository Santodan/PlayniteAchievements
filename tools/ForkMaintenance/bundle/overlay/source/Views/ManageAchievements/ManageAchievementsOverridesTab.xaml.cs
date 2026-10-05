using System;
using System.IO;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PlayniteAchievements.Providers;

namespace PlayniteAchievements.Views.ManageAchievements
{
    public partial class ManageAchievementsOverridesTab : UserControl
    {
        public ManageAchievementsOverridesTab()
        {
            InitializeComponent();
            Loaded += (_, __) => RefreshLocalProviderIcon();
        }

        private void RefreshLocalProviderIcon()
        {
            var provider = ProviderRegistry.Instance?.GetProvider("Local");
            var iconKey = provider?.ProviderIconKey ?? "ProviderIconLocal";
            var colorHex = ProviderRegistry.GetProviderColorHex("Local", provider?.ProviderColorHex ?? "#FF8A00");
            try
            {
                if (Path.IsPathRooted(iconKey) && File.Exists(iconKey))
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                    bitmap.UriSource = new Uri(iconKey, UriKind.Absolute);
                    bitmap.EndInit();
                    bitmap.Freeze();
                    LocalProviderNavigationIcon.Source = bitmap;
                    return;
                }

                var geometry = TryFindResource("Geo" + iconKey.Replace("ProviderIcon", string.Empty)) as Geometry;
                if (geometry != null)
                {
                    var image = new DrawingImage(new GeometryDrawing(
                        new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorHex)), null, geometry));
                    image.Freeze();
                    LocalProviderNavigationIcon.Source = image;
                    return;
                }
            }
            catch (Exception)
            {
                // An unreadable custom image must not prevent the Overrides page from opening.
            }

            LocalProviderNavigationIcon.Source = new DrawingImage(new GeometryDrawing(
                new SolidColorBrush(Color.FromRgb(255, 138, 0)), null, (Geometry)FindResource("GeoLocal")));
        }
    }
}
