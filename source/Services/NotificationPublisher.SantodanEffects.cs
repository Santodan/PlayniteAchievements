using System;
using System.Windows;
using System.Windows.Media;
using Playnite.SDK.Models;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.Local;

namespace PlayniteAchievements.Services
{
    public partial class NotificationPublisher
    {
        private static bool ReducedSantodanNotificationEffects =>
            ProviderRegistry.Settings<LocalSettings>().ReduceForkNotificationEffects;

        private static string SantodanReducedEffectsCss(LocalSettings settings) =>
            settings?.ReduceForkNotificationEffects == true
                ? "*,*::before,*::after{box-shadow:none!important;text-shadow:none!important;filter:none!important;backdrop-filter:none!important;-webkit-text-stroke:0!important;transition:none!important;animation-duration:0.001ms!important;animation-delay:0s!important;animation-iteration-count:1!important;}"
                : string.Empty;

        private FrameworkElement BuildOverlayContent(string title, string gameName,
            string achievementName, string rawIconPath, string style, string providerKey,
            LocalSettings localSettings, double overlayScale, Game game = null,
            string achievementDescription = null, int? achievementPoints = null,
            string achievementRarity = null, string achievementTrophy = null)
        {
            var root = BuildOverlayContentCore(title, gameName, achievementName, rawIconPath,
                style, providerKey, localSettings, overlayScale, game, achievementDescription,
                achievementPoints, achievementRarity, achievementTrophy);
            if (root != null && localSettings?.ReduceForkNotificationEffects == true)
            {
                StripSantodanEffects(root);
                RoutedEventHandler loaded = null;
                loaded = (_, __) =>
                {
                    root.Loaded -= loaded;
                    StripSantodanEffects(root);
                };
                root.Loaded += loaded;
            }
            return root;
        }

        private static void StripSantodanEffects(DependencyObject node)
        {
            if (node is UIElement element) element.Effect = null;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                StripSantodanEffects(VisualTreeHelper.GetChild(node, i));
        }
    }
}
