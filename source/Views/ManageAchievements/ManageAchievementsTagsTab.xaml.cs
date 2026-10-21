using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.ViewModels.Tags;

namespace PlayniteAchievements.Views.ManageAchievements
{
    /// <summary>
    /// Manage Achievements: the tags only this game offers, its stored ones and those only its
    /// achievement data defines. Each edit is stored at once; Make Global moves a tag into the
    /// global list, keeping its key so tagged achievements keep it.
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

            // A game's copy of a key the global list holds is shadowed by the global one, so it is
            // not listed, and the next edit drops it.
            var current = (store.TryLoad(gameId, out var data) ? data?.AchievementTags : null)?
                .Where(definition => !IsGlobal(definition.Key))
                .ToList();
            var fetched = FetchedTags(gameId, data);
            Editor.DataContext = new AchievementTagListViewModel(
                current,
                definitions => store.Update(
                    gameId,
                    customData => customData.AchievementTags = definitions.Count > 0 ? definitions.ToList() : null,
                    affectsSummaryData: false,
                    affectsOverrideMirror: false),
                value => PlayniteAchievementsPlugin.Instance?.PickColor(Window.GetWindow(this), value),
                definition => MakeGlobal(definition, gameId, store, settings, saveSettings),
                fetched);

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

        /// <summary>
        /// The tags the game's achievements carry, from their providers, sources or overrides, that
        /// neither the global list nor the game defines, in tag order.
        /// </summary>
        private static List<AchievementTagDefinition> FetchedTags(Guid gameId, Models.Settings.GameCustomDataFile data)
        {
            var keys = new List<string>();
            var achievements = PlayniteAchievementsPlugin.Instance?.CacheManager?.LoadGameData(gameId.ToString())?.Achievements;
            foreach (var achievement in achievements ?? Enumerable.Empty<AchievementDetail>())
            {
                keys.AddRange(AchievementTagHelper.GetComponents(achievement?.Tags));
            }

            foreach (var pair in data?.AchievementOverrides ?? new Dictionary<string, Models.Settings.AchievementOverride>())
            {
                keys.AddRange(AchievementTagHelper.GetComponents(pair.Value?.Tags));
            }

            return AchievementTagHelper.Parse(string.Join("|", keys))
                .Where(key => AchievementTagRegistry.IsImplicit(key, gameId))
                .Select(key => new AchievementTagDefinition { Key = key })
                .ToList();
        }

        /// <summary>Opens Settings on the Metadata tab's Tags page, where the global list is edited.</summary>
        private void OpenGlobalTagsSettings_Click(object sender, RoutedEventArgs e)
        {
            PlayniteAchievementsPlugin.Instance?.OpenSettingsAt(
                new Models.Settings.SettingsNavigationRequest(
                    Models.Settings.SettingsNavigationRequest.MetadataTab,
                    Models.Settings.SettingsNavigationRequest.TagsPage));
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

        private static bool IsGlobal(string key) =>
            AchievementTagRegistry.Global.Any(definition => string.Equals(definition.Key, key, StringComparison.OrdinalIgnoreCase));

        private static void MakeGlobal(
            AchievementTagDefinition definition,
            Guid gameId,
            GameCustomDataStore store,
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
            OfferToCombineByName(definition, gameId, store);
        }

        /// <summary>
        /// When other games have their own tag with the promoted tag's name, asks whether to fold
        /// them into it: their copies go, and their achievements' tag edits take the global key.
        /// </summary>
        private static void OfferToCombineByName(AchievementTagDefinition promoted, Guid gameId, GameCustomDataStore store)
        {
            var name = AchievementTagRegistry.GetName(promoted)?.Trim();
            if (store == null || string.IsNullOrEmpty(name))
            {
                return;
            }

            bool SameName(AchievementTagDefinition definition) =>
                definition != null &&
                !string.Equals(definition.Key, promoted.Key, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(AchievementTagRegistry.GetName(definition)?.Trim(), name, StringComparison.OrdinalIgnoreCase);

            var games = store.QueryAll(files => files
                .Where(file => file != null && file.PlayniteGameId != gameId && file.AchievementTags?.Any(SameName) == true)
                .Select(file => file.PlayniteGameId)
                .ToList());
            if (games.Count == 0)
            {
                return;
            }

            var answer = PlayniteAchievementsPlugin.Instance?.PlayniteApi?.Dialogs?.ShowMessage(
                string.Format(ResourceProvider.GetString("LOCPlayAch_ManageAchievements_Tags_CombineByName"), games.Count, name),
                ResourceProvider.GetString("LOCPlayAch_ManageAchievements_Tags_MakeGlobal"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
            {
                return;
            }

            foreach (var other in games)
            {
                store.Update(
                    other,
                    customData =>
                    {
                        var merged = customData.AchievementTags?.Where(SameName).Select(definition => definition.Key).ToList() ??
                                     new List<string>();
                        customData.AchievementTags = customData.AchievementTags?.Where(definition => !SameName(definition)).ToList();
                        if (customData.AchievementTags?.Count == 0)
                        {
                            customData.AchievementTags = null;
                        }

                        foreach (var entry in customData.AchievementOverrides?.Values ?? Enumerable.Empty<Models.Settings.AchievementOverride>())
                        {
                            if (entry?.Tags == null || !merged.Any(key => AchievementTagHelper.Contains(entry.Tags, key)))
                            {
                                continue;
                            }

                            var tags = merged.Aggregate(entry.Tags, (value, key) => AchievementTagHelper.WithTag(value, key, include: false));
                            entry.Tags = AchievementTagHelper.WithTag(tags, promoted.Key, include: true) ?? string.Empty;
                        }
                    },
                    affectsSummaryData: false);
            }
        }
    }
}
