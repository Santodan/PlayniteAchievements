using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.ViewModels.Tags;

namespace PlayniteAchievements.Views.ManageAchievements
{
    /// <summary>
    /// Manage Achievements: the tags only this game offers. Each edit is stored at once; Make
    /// Global moves a tag into the global list, keeping its key so tagged achievements keep it.
    /// </summary>
    public partial class ManageAchievementsTagsTab : UserControl
    {
        public ManageAchievementsTagsTab()
        {
            InitializeComponent();
        }

        internal ManageAchievementsTagsTab(
            Guid gameId,
            GameCustomDataStore store,
            PlayniteAchievementsSettings settings,
            Action<PlayniteAchievementsSettings> saveSettings)
            : this()
        {
            if (store == null) throw new ArgumentNullException(nameof(store));

            var current = store.TryLoad(gameId, out var data) ? data?.AchievementTags : null;
            Editor.DataContext = new AchievementTagListViewModel(
                current,
                definitions => store.Update(
                    gameId,
                    customData => customData.AchievementTags = definitions.Count > 0 ? definitions.ToList() : null,
                    affectsSummaryData: false,
                    affectsOverrideMirror: false),
                value => PlayniteAchievementsPlugin.Instance?.PickColor(Window.GetWindow(this), value),
                definition => MakeGlobal(definition, settings, saveSettings));

            ShowGlobalTags();
            // The global list changes when a tag is made global here or settings are saved.
            AchievementTagRegistry.Changed += OnTagsChanged;
            Unloaded += (_, __) => AchievementTagRegistry.Changed -= OnTagsChanged;
            Loaded += (_, __) =>
            {
                AchievementTagRegistry.Changed -= OnTagsChanged;
                AchievementTagRegistry.Changed += OnTagsChanged;
                ShowGlobalTags();
            };
        }

        /// <summary>Opens Settings on the Metadata tab, where the global list is edited.</summary>
        private void OpenGlobalTagsSettings_Click(object sender, RoutedEventArgs e)
        {
            PlayniteAchievementsPlugin.Instance?.OpenSettingsAt(
                new Models.Settings.SettingsNavigationRequest(Models.Settings.SettingsNavigationRequest.MetadataTab));
        }

        private void ShowGlobalTags()
        {
            GlobalTags.ItemsSource = AchievementTagRegistry.Global
                .Where(definition => !AchievementTagHelper.IsDerivedTag(definition.Key))
                .Select(definition => new AchievementTagPreviewRow(definition))
                .ToList();
        }

        private void OnTagsChanged(object sender, AchievementTagsChangedEventArgs e)
        {
            if (e?.GameId == null)
            {
                Dispatcher.BeginInvoke(new Action(ShowGlobalTags));
            }
        }

        private static void MakeGlobal(
            AchievementTagDefinition definition,
            PlayniteAchievementsSettings settings,
            Action<PlayniteAchievementsSettings> saveSettings)
        {
            var persisted = settings?.Persisted;
            if (definition == null || persisted == null)
            {
                return;
            }

            var global = new List<AchievementTagDefinition>(persisted.ResolveAchievementTags());
            if (global.All(existing => !string.Equals(existing.Key, definition.Key, StringComparison.OrdinalIgnoreCase)))
            {
                global.Add(definition.Clone());
            }

            persisted.AchievementTags = global;
            saveSettings?.Invoke(settings);
            AchievementTagRegistry.SetGlobal(persisted.ResolveAchievementTags());
        }
    }
}
