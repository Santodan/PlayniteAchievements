using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Playnite.SDK;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Showcase;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>Builds the collection-aware pin submenu shared by plugin-owned row menus.</summary>
    internal static class ShowcasePinMenuBuilder
    {
        public static void AppendAchievementMenu(
            ContextMenu menu,
            FrameworkElement resourceOwner,
            Guid gameId,
            string apiName,
            string gameName,
            string achievementName)
        {
            var showcase = CurrentSettings;
            if (menu == null || showcase == null)
            {
                return;
            }

            var parent = CreateParent(resourceOwner);
            foreach (var collection in showcase.AchievementPinCollections
                .Where(collection => collection != null)
                .ToList())
            {
                var captured = collection;
                parent.Items.Add(CreateCollectionItem(
                    resourceOwner,
                    captured.Name,
                    ShowcasePinService.IsAchievementPinned(showcase, captured.CollectionId, gameId, apiName),
                    () =>
                    {
                        ShowcasePinService.ToggleAchievement(
                            showcase,
                            captured.CollectionId,
                            gameId,
                            apiName,
                            gameName,
                            achievementName);
                        ShowcaseConfigurationCommit.Commit();
                    },
                    () => RenameAchievementCollection(resourceOwner, captured),
                    () => ClearOrDeleteAchievementCollection(resourceOwner, captured),
                    ShowcasePinService.IsDefaultAchievementCollection(showcase, captured.CollectionId)));
            }

            parent.Items.Add(new Separator());
            parent.Items.Add(CreateLocalizedMenuItem(resourceOwner, "LOCPlayAch_Showcase_NewCollection", () =>
            {
                var collection = PromptForNewAchievementCollection(resourceOwner);
                if (collection == null)
                {
                    return;
                }

                ShowcasePinService.ToggleAchievement(
                    showcase,
                    collection.CollectionId,
                    gameId,
                    apiName,
                    gameName,
                    achievementName);
                ShowcaseConfigurationCommit.Commit();
            }));
            menu.Items.Add(parent);
        }

        public static void AppendGameMenu(
            ContextMenu menu,
            FrameworkElement resourceOwner,
            Guid gameId)
        {
            var showcase = CurrentSettings;
            if (menu == null || showcase == null || gameId == Guid.Empty)
            {
                return;
            }

            var parent = CreateParent(resourceOwner);
            foreach (var collection in showcase.GamePinCollections
                .Where(collection => collection != null)
                .ToList())
            {
                var captured = collection;
                parent.Items.Add(CreateCollectionItem(
                    resourceOwner,
                    captured.Name,
                    ShowcasePinService.IsGamePinned(showcase, captured.CollectionId, gameId),
                    () =>
                    {
                        ShowcasePinService.ToggleGame(showcase, captured.CollectionId, gameId);
                        ShowcaseConfigurationCommit.Commit();
                    },
                    () => RenameGameCollection(resourceOwner, captured),
                    () => ClearOrDeleteGameCollection(resourceOwner, captured),
                    ShowcasePinService.IsDefaultGameCollection(showcase, captured.CollectionId)));
            }

            parent.Items.Add(new Separator());
            parent.Items.Add(CreateLocalizedMenuItem(resourceOwner, "LOCPlayAch_Showcase_NewCollection", () =>
            {
                var collection = PromptForNewGameCollection(resourceOwner);
                if (collection == null)
                {
                    return;
                }

                ShowcasePinService.ToggleGame(showcase, collection.CollectionId, gameId);
                ShowcaseConfigurationCommit.Commit();
            }));
            menu.Items.Add(parent);
        }

        private static ShowcaseSettings CurrentSettings =>
            PlayniteAchievementsPlugin.Instance?.Settings?.Persisted?.Showcase;

        private static MenuItem CreateParent(FrameworkElement resourceOwner) =>
            new MenuItem { Header = L(resourceOwner, "LOCPlayAch_Showcase_PinToShowcase") };

        private static MenuItem CreateCollectionItem(
            FrameworkElement resourceOwner,
            string name,
            bool isPinned,
            Action toggle,
            Action rename,
            Action clearOrDelete,
            bool isDefault)
        {
            var item = new MenuItem
            {
                IsCheckable = true,
                IsChecked = isPinned,
                Header = CreateCollectionHeader(
                    resourceOwner,
                    name,
                    rename,
                    clearOrDelete,
                    isDefault)
            };
            item.Click += (_, __) => toggle?.Invoke();
            return item;
        }

        private static FrameworkElement CreateCollectionHeader(
            FrameworkElement resourceOwner,
            string name,
            Action rename,
            Action clearOrDelete,
            bool isDefault)
        {
            var row = new Grid { MinWidth = 250 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var label = new TextBlock
            {
                Text = name,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "PlayAch.Brush.Text");
            row.Children.Add(label);

            var edit = CreateGlyphButton(
                "\uE70F",
                L(resourceOwner, "LOCPlayAch_Showcase_RenameCollection"),
                rename);
            Grid.SetColumn(edit, 1);
            row.Children.Add(edit);

            var delete = CreateGlyphButton(
                "\uE74D",
                L(
                    resourceOwner,
                    isDefault
                        ? "LOCPlayAch_Showcase_ClearCollection"
                        : "LOCPlayAch_Showcase_DeleteCollection"),
                clearOrDelete);
            Grid.SetColumn(delete, 2);
            row.Children.Add(delete);
            return row;
        }

        private static Button CreateGlyphButton(string glyph, string label, Action action)
        {
            var button = new Button
            {
                Content = new TextBlock
                {
                    Text = glyph,
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    VerticalAlignment = VerticalAlignment.Center
                },
                ToolTip = label,
                MinWidth = 28,
                MinHeight = 24,
                Padding = new Thickness(4, 1, 4, 1),
                Margin = new Thickness(2, 0, 0, 0)
            };
            AutomationProperties.SetName(button, label);
            button.Click += (_, e) =>
            {
                e.Handled = true;
                action?.Invoke();
            };
            return button;
        }

        private static MenuItem CreateLocalizedMenuItem(
            FrameworkElement resourceOwner,
            string key,
            Action action)
        {
            var item = new MenuItem { Header = L(resourceOwner, key) };
            item.Click += (_, __) => action?.Invoke();
            return item;
        }

        private static PinnedAchievementCollection PromptForNewAchievementCollection(
            FrameworkElement resourceOwner)
        {
            while (true)
            {
                var name = PromptForName(resourceOwner, null, newCollection: true);
                if (name == null)
                {
                    return null;
                }

                if (ShowcasePinService.TryCreateAchievementCollection(CurrentSettings, name, out var collection))
                {
                    return collection;
                }

                ShowInvalidName(resourceOwner);
            }
        }

        private static PinnedGameCollection PromptForNewGameCollection(FrameworkElement resourceOwner)
        {
            while (true)
            {
                var name = PromptForName(resourceOwner, null, newCollection: true);
                if (name == null)
                {
                    return null;
                }

                if (ShowcasePinService.TryCreateGameCollection(CurrentSettings, name, out var collection))
                {
                    return collection;
                }

                ShowInvalidName(resourceOwner);
            }
        }

        private static void RenameAchievementCollection(
            FrameworkElement resourceOwner,
            PinnedAchievementCollection collection)
        {
            while (collection != null)
            {
                var name = PromptForName(resourceOwner, collection.Name, newCollection: false);
                if (name == null)
                {
                    return;
                }

                if (ShowcasePinService.RenameAchievementCollection(
                    CurrentSettings,
                    collection.CollectionId,
                    name))
                {
                    ShowcaseConfigurationCommit.Commit();
                    return;
                }

                ShowInvalidName(resourceOwner);
            }
        }

        private static void RenameGameCollection(
            FrameworkElement resourceOwner,
            PinnedGameCollection collection)
        {
            while (collection != null)
            {
                var name = PromptForName(resourceOwner, collection.Name, newCollection: false);
                if (name == null)
                {
                    return;
                }

                if (ShowcasePinService.RenameGameCollection(
                    CurrentSettings,
                    collection.CollectionId,
                    name))
                {
                    ShowcaseConfigurationCommit.Commit();
                    return;
                }

                ShowInvalidName(resourceOwner);
            }
        }

        private static void ClearOrDeleteAchievementCollection(
            FrameworkElement resourceOwner,
            PinnedAchievementCollection collection)
        {
            var showcase = CurrentSettings;
            if (showcase == null || collection == null)
            {
                return;
            }

            var isDefault = ShowcasePinService.IsDefaultAchievementCollection(
                showcase,
                collection.CollectionId);
            if (!isDefault && !ConfirmCollectionDeletion(resourceOwner, collection.Name))
            {
                return;
            }

            var changed = isDefault
                ? ShowcasePinService.ClearAchievementCollection(showcase, collection.CollectionId)
                : ShowcasePinService.DeleteAchievementCollection(showcase, collection.CollectionId);
            if (changed)
            {
                ShowcaseConfigurationCommit.Commit();
            }
        }

        private static void ClearOrDeleteGameCollection(
            FrameworkElement resourceOwner,
            PinnedGameCollection collection)
        {
            var showcase = CurrentSettings;
            if (showcase == null || collection == null)
            {
                return;
            }

            var isDefault = ShowcasePinService.IsDefaultGameCollection(showcase, collection.CollectionId);
            if (!isDefault && !ConfirmCollectionDeletion(resourceOwner, collection.Name))
            {
                return;
            }

            var changed = isDefault
                ? ShowcasePinService.ClearGameCollection(showcase, collection.CollectionId)
                : ShowcasePinService.DeleteGameCollection(showcase, collection.CollectionId);
            if (changed)
            {
                ShowcaseConfigurationCommit.Commit();
            }
        }

        private static bool ConfirmCollectionDeletion(
            FrameworkElement resourceOwner,
            string collectionName)
        {
            return API.Instance?.Dialogs?.ShowMessage(
                string.Format(
                    L(resourceOwner, "LOCPlayAch_Showcase_DeleteCollectionConfirm"),
                    collectionName),
                L(resourceOwner, "LOCPlayAch_Title_PluginName"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) == MessageBoxResult.Yes;
        }

        private static string PromptForName(
            FrameworkElement resourceOwner,
            string currentName,
            bool newCollection)
        {
            var result = API.Instance?.Dialogs?.SelectString(
                L(
                    resourceOwner,
                    newCollection
                        ? "LOCPlayAch_Showcase_NewCollectionPrompt"
                        : "LOCPlayAch_Showcase_RenameCollectionPrompt"),
                L(
                    resourceOwner,
                    newCollection
                        ? "LOCPlayAch_Showcase_NewCollection"
                        : "LOCPlayAch_Showcase_RenameCollection"),
                currentName ?? string.Empty);
            return result?.Result == true ? result.SelectedString : null;
        }

        private static void ShowInvalidName(FrameworkElement resourceOwner)
        {
            API.Instance?.Dialogs?.ShowMessage(
                L(resourceOwner, "LOCPlayAch_Showcase_CollectionNameInvalid"),
                L(resourceOwner, "LOCPlayAch_Title_PluginName"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        private static string L(FrameworkElement resourceOwner, string key) =>
            resourceOwner?.TryFindResource(key) as string
            ?? ResourceProvider.GetString(key)
            ?? key;
    }
}
