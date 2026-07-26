using System;
using System.Windows;
using System.Windows.Threading;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;

namespace PlayniteAchievements.Services.UI
{
    internal sealed class AchievementResourceService
    {
        private readonly ILogger _logger;
        private volatile bool _loaded;

        public AchievementResourceService(ILogger logger)
        {
            _logger = logger;
        }

        public void EnsureAchievementResourcesLoaded(PlayniteAchievementsSettings settings)
        {
            // This runs per theme-control creation (once per visible library tile), and the
            // token/badge writes below mutate Application.Current.Resources — an app-wide
            // DynamicResource invalidation. Apply once; settings changes reapply through
            // SettingsViewModel.ApplyThemeResources and PersistedSettings_PropertyChanged.
            if (_loaded)
            {
                return;
            }

            try
            {
                var app = Application.Current;
                if (app == null)
                {
                    return;
                }

                void LoadResources()
                {
                    PlayAchResourceService.Apply(
                        app.Resources,
                        settings?.Persisted?.ResourceOverrides,
                        settings?.Persisted);
                    EnsureMergedDictionaryLoaded(app.Resources, "/PlayniteAchievements;component/Resources/DesignTokens.xaml");
                    EnsureMergedDictionaryLoaded(app.Resources, "/PlayniteAchievements;component/Resources/CommonResources.xaml");
                    EnsureMergedDictionaryLoaded(app.Resources, "/PlayniteAchievements;component/Resources/RarityBadges.xaml");
                    EnsureMergedDictionaryLoaded(app.Resources, "/PlayniteAchievements;component/Resources/TrophyBadges.xaml");
                    EnsureMergedDictionaryLoaded(app.Resources, "/PlayniteAchievements;component/Resources/AchievementResources.xaml");
                    EnsureMergedDictionaryLoaded(app.Resources, "/PlayniteAchievements;component/Providers/ProviderIcons.xaml");
                    EnsureMergedDictionaryLoaded(app.Resources, "/PlayniteAchievements;component/Providers/ProviderSettingsStyles.xaml");
                    EnsureMergedDictionaryLoaded(app.Resources, "/PlayniteAchievements;component/Resources/ManageAchievementsStyles.xaml");
                    EnsureMergedDictionaryLoaded(app.Resources, "/PlayniteAchievements;component/Resources/OverviewStyles.xaml");
                    EnsureMergedDictionaryLoaded(app.Resources, "/PlayniteAchievements;component/Resources/SettingsStyles.xaml");
                    EnsureMergedDictionaryLoaded(app.Resources, "/PlayniteAchievements;component/Resources/MigrationStyles.xaml");
                    RarityAppearanceHelper.ApplyBadgeApplicationResources(settings?.Persisted);
                }

                if (app.Dispatcher.CheckAccess())
                {
                    LoadResources();
                }
                else
                {
                    app.Dispatcher.Invoke(LoadResources, DispatcherPriority.Normal);
                }

                _loaded = true;
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to load achievement resources at application level.");
            }
        }

        private static void EnsureMergedDictionaryLoaded(ResourceDictionary resources, string relativeUri)
        {
            if (resources == null || string.IsNullOrWhiteSpace(relativeUri))
            {
                return;
            }

            var targetUri = new Uri(relativeUri, UriKind.Relative);
            foreach (var dictionary in resources.MergedDictionaries)
            {
                if (dictionary?.Source == null)
                {
                    continue;
                }

                if (Uri.Compare(dictionary.Source, targetUri, UriComponents.SerializationInfoString, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    return;
                }
            }

            resources.MergedDictionaries.Add(new ResourceDictionary { Source = targetUri });
        }
    }
}
