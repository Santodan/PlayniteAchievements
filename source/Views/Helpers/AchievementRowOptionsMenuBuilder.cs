using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Playnite.SDK;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.UI;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.Views;

namespace PlayniteAchievements.Views.Helpers
{
    internal static class AchievementRowOptionsMenuBuilder
    {
        public static bool AppendAchievementOptions(
            ContextMenu menu,
            object data,
            FrameworkElement resourceOwner,
            Action onChanged)
        {
            if (menu == null || !AchievementRowContext.TryCreate(data, out var context))
            {
                return false;
            }

            if (menu.Items.Count > 0)
            {
                menu.Items.Add(new Separator());
            }

            menu.Items.Add(CreateSetCapstoneItem(context, resourceOwner, onChanged));
            menu.Items.Add(CreateCategoriesMenu(context, resourceOwner, onChanged));
            menu.Items.Add(CreateFiltersMenu(context, resourceOwner, onChanged));
            menu.Items.Add(CreateNotesMenu(context, resourceOwner, onChanged));
            return true;
        }

        private static MenuItem CreateSetCapstoneItem(
            AchievementRowContext context,
            FrameworkElement resourceOwner,
            Action onChanged)
        {
            var manualCapstone = GameCustomDataLookup.GetManualCapstone(
                context.GameId,
                CurrentSettings,
                CurrentStore);
            var isManualCapstone = string.Equals(
                manualCapstone,
                context.ApiName,
                StringComparison.OrdinalIgnoreCase);
            var isEffectiveCapstone = context.IsCapstone || isManualCapstone;

            var item = new MenuItem
            {
                Header = L(resourceOwner, "LOCPlayAch_Menu_SetCapstone", "Set Capstone"),
                IsCheckable = true,
                IsChecked = isEffectiveCapstone
            };
            item.Click += (_, __) =>
            {
                var service = CurrentOverridesService;
                if (service == null)
                {
                    return;
                }

                var result = service.SetCapstone(
                    context.GameId,
                    isEffectiveCapstone ? null : context.ApiName);
                if (!result.Success)
                {
                    ShowError(result.ErrorMessage);
                    return;
                }

                onChanged?.Invoke();
            };

            return item;
        }

        private static MenuItem CreateCategoriesMenu(
            AchievementRowContext context,
            FrameworkElement resourceOwner,
            Action onChanged)
        {
            var menu = new MenuItem
            {
                Header = L(resourceOwner, "LOCPlayAch_ManageAchievements_Tab_Category", "Categories")
            };

            var addTypeMenu = new MenuItem
            {
                Header = L(resourceOwner, "LOCPlayAch_Common_AddType", "Add Type")
            };
            foreach (var categoryType in AchievementCategoryTypeHelper.AllowedCategoryTypes.Where(type =>
                         !string.Equals(type, AchievementCategoryTypeHelper.DefaultCategoryType, StringComparison.OrdinalIgnoreCase)))
            {
                var capturedType = categoryType;
                addTypeMenu.Items.Add(CreateMenuItem(
                    ManageAchievementsCategoryViewModel.GetCategoryTypeDisplayName(capturedType),
                    () =>
                    {
                        AddCategoryType(context, capturedType);
                        onChanged?.Invoke();
                    }));
            }

            menu.Items.Add(addTypeMenu);
            menu.Items.Add(CreateMenuItem(
                L(resourceOwner, "LOCPlayAch_Common_SetLabelEllipsis", "Set Label..."),
                () =>
                {
                    if (SetCategoryLabel(context, resourceOwner))
                    {
                        onChanged?.Invoke();
                    }
                }));
            menu.Items.Add(CreateMenuItem(
                L(resourceOwner, "LOCPlayAch_Button_Clear", "Clear"),
                () =>
                {
                    if (ClearCategories(context))
                    {
                        onChanged?.Invoke();
                    }
                }));

            return menu;
        }

        private static MenuItem CreateFiltersMenu(
            AchievementRowContext context,
            FrameworkElement resourceOwner,
            Action onChanged)
        {
            var filtered = GameCustomDataLookup.GetFilteredAchievementApiNames(
                context.GameId,
                CurrentSettings,
                CurrentStore);
            var summaryFiltered = GameCustomDataLookup.GetSummaryFilteredAchievementApiNames(
                context.GameId,
                CurrentSettings,
                CurrentStore);
            var isFiltered = filtered.Contains(context.ApiName);
            var isSummaryFiltered = summaryFiltered.Contains(context.ApiName);

            var menu = new MenuItem
            {
                Header = L(resourceOwner, "LOCPlayAch_Menu_Filters", "Filters")
            };

            var filterOutItem = new MenuItem
            {
                Header = L(resourceOwner, "LOCPlayAch_ManageAchievements_Filters_FilterOut", "Filter Out"),
                IsCheckable = true,
                IsChecked = isFiltered
            };
            filterOutItem.Click += (_, __) =>
            {
                SetFilterState(context, setFullFilter: !isFiltered, setSummaryFilter: isSummaryFiltered);
                onChanged?.Invoke();
            };
            menu.Items.Add(filterOutItem);

            var summaryItem = new MenuItem
            {
                Header = L(resourceOwner, "LOCPlayAch_ManageAchievements_Filters_FilterOutOfSummaries", "Filter Out of Summaries"),
                IsCheckable = true,
                IsChecked = isFiltered || isSummaryFiltered,
                IsEnabled = !isFiltered
            };
            summaryItem.Click += (_, __) =>
            {
                if (!isFiltered)
                {
                    SetFilterState(context, setFullFilter: false, setSummaryFilter: !isSummaryFiltered);
                    onChanged?.Invoke();
                }
            };
            menu.Items.Add(summaryItem);

            return menu;
        }

        private static MenuItem CreateNotesMenu(
            AchievementRowContext context,
            FrameworkElement resourceOwner,
            Action onChanged)
        {
            var note = GameCustomDataLookup.GetAchievementNote(
                context.GameId,
                context.ApiName,
                CurrentSettings,
                CurrentStore);
            var hasNote = !string.IsNullOrWhiteSpace(note);

            var menu = new MenuItem
            {
                Header = L(resourceOwner, "LOCPlayAch_ManageAchievements_Tab_Notes", "Notes")
            };

            var viewItem = CreateMenuItem(
                L(resourceOwner, "LOCPlayAch_Common_View", "View"),
                () => OpenNoteDialog(context, note, isEditMode: false, resourceOwner, onChanged));
            viewItem.IsEnabled = hasNote;
            menu.Items.Add(viewItem);

            menu.Items.Add(CreateMenuItem(
                L(resourceOwner, "LOCPlayAch_Common_Edit", "Edit"),
                () => OpenNoteDialog(context, note, isEditMode: true, resourceOwner, onChanged)));

            return menu;
        }

        private static void AddCategoryType(AchievementRowContext context, string categoryType)
        {
            var map = GameCustomDataLookup.GetAchievementCategoryTypeOverrides(
                context.GameId,
                CurrentSettings,
                CurrentStore);
            var normalizedMap = CloneStringMap(map);
            var currentEffective = AchievementCategoryTypeHelper.NormalizeOrDefault(context.CategoryType);
            var merged = AchievementCategoryTypeHelper.NormalizeOrDefault(
                AchievementCategoryTypeHelper.Combine(
                    AchievementCategoryTypeHelper.ParseValues(currentEffective)
                        .Concat(new[] { categoryType })));
            if (string.Equals(merged, currentEffective, StringComparison.Ordinal))
            {
                return;
            }

            normalizedMap[context.ApiName] = merged;
            CurrentOverridesService?.SetAchievementCategoryTypeOverrides(context.GameId, normalizedMap);
            context.ApplyCategoryType(merged);
        }

        private static bool SetCategoryLabel(
            AchievementRowContext context,
            FrameworkElement resourceOwner)
        {
            var inputDialog = new TextInputDialog(
                L(resourceOwner, "LOCPlayAch_ManageAchievements_Category_Context_SetLabelHint", "Enter a category label for the selected achievements."),
                context.CategoryLabel);
            var window = PlayniteUiProvider.CreateExtensionWindow(
                L(resourceOwner, "LOCPlayAch_ManageAchievements_Category_Context_SetLabelTitle", "Set Category Label"),
                inputDialog,
                new WindowOptions
                {
                    ShowMinimizeButton = false,
                    ShowMaximizeButton = false,
                    ShowCloseButton = true,
                    CanBeResizable = false,
                    Width = 500,
                    Height = 200
                });

            inputDialog.RequestClose += (s, e) => window.Close();
            window.ShowDialog();

            if (inputDialog.DialogResult != true)
            {
                return false;
            }

            var normalizedCategory = AchievementCategoryTypeHelper.NormalizeCategory(inputDialog.InputText);
            if (string.IsNullOrWhiteSpace(normalizedCategory))
            {
                return false;
            }

            var map = GameCustomDataLookup.GetAchievementCategoryOverrides(
                context.GameId,
                CurrentSettings,
                CurrentStore);
            var normalizedMap = CloneStringMap(map);
            normalizedMap[context.ApiName] = normalizedCategory;
            CurrentOverridesService?.SetAchievementCategoryOverrides(context.GameId, normalizedMap);
            context.ApplyCategoryLabel(normalizedCategory);
            return true;
        }

        private static bool ClearCategories(AchievementRowContext context)
        {
            var changed = false;
            var categoryMap = CloneStringMap(GameCustomDataLookup.GetAchievementCategoryOverrides(
                context.GameId,
                CurrentSettings,
                CurrentStore));
            var typeMap = CloneStringMap(GameCustomDataLookup.GetAchievementCategoryTypeOverrides(
                context.GameId,
                CurrentSettings,
                CurrentStore));

            changed |= categoryMap.Remove(context.ApiName);
            changed |= typeMap.Remove(context.ApiName);
            if (!changed)
            {
                return false;
            }

            var service = CurrentOverridesService;
            service?.SetAchievementCategoryOverrides(context.GameId, categoryMap);
            service?.SetAchievementCategoryTypeOverrides(context.GameId, typeMap);
            return true;
        }

        private static void SetFilterState(
            AchievementRowContext context,
            bool setFullFilter,
            bool setSummaryFilter)
        {
            var filtered = GameCustomDataLookup.GetFilteredAchievementApiNames(
                context.GameId,
                CurrentSettings,
                CurrentStore);
            var summaryFiltered = GameCustomDataLookup.GetSummaryFilteredAchievementApiNames(
                context.GameId,
                CurrentSettings,
                CurrentStore);

            if (setFullFilter)
            {
                filtered.Add(context.ApiName);
                summaryFiltered.Remove(context.ApiName);
            }
            else
            {
                filtered.Remove(context.ApiName);
                if (setSummaryFilter)
                {
                    summaryFiltered.Add(context.ApiName);
                }
                else
                {
                    summaryFiltered.Remove(context.ApiName);
                }
            }

            CurrentOverridesService?.SetAchievementFilters(
                context.GameId,
                filtered,
                summaryFiltered);
        }

        private static void OpenNoteDialog(
            AchievementRowContext context,
            string note,
            bool isEditMode,
            FrameworkElement resourceOwner,
            Action onChanged)
        {
            var dialog = new AchievementNoteDialog(
                context.DisplayName,
                context.ApiName,
                note,
                isReadOnly: !isEditMode,
                achievementIconSource: context.DisplayIcon);
            var title = isEditMode
                ? L(resourceOwner, "LOCPlayAch_NotesDialog_EditTitle", "Edit Note")
                : L(resourceOwner, "LOCPlayAch_NotesDialog_ViewTitle", "View Note");
            var window = PlayniteUiProvider.CreateExtensionWindow(
                title,
                dialog,
                new WindowOptions
                {
                    ShowMinimizeButton = false,
                    ShowMaximizeButton = false,
                    ShowCloseButton = true,
                    CanBeResizable = true,
                    Width = 640,
                    Height = isEditMode ? 560 : 420
                });

            dialog.RequestClose += (s, e) => window.Close();
            window.ShowDialog();

            if (!isEditMode || dialog.DialogResult != true)
            {
                return;
            }

            CurrentOverridesService?.SetAchievementNote(
                context.GameId,
                context.ApiName,
                dialog.SavedNote);
            context.ApplyNote(dialog.SavedNote);
            onChanged?.Invoke();
        }

        private static MenuItem CreateMenuItem(string header, Action onClick)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, __) => onClick?.Invoke();
            return item;
        }

        private static Dictionary<string, string> CloneStringMap(IDictionary<string, string> source)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (source == null)
            {
                return map;
            }

            foreach (var pair in source)
            {
                var key = (pair.Key ?? string.Empty).Trim();
                var value = (pair.Value ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
                {
                    map[key] = value;
                }
            }

            return map;
        }

        private static void ShowError(string message)
        {
            API.Instance.Dialogs.ShowMessage(
                string.IsNullOrWhiteSpace(message)
                    ? ResourceProvider.GetString("LOCPlayAch_Error_RebuildFailed")
                    : message,
                ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }

        private static string L(FrameworkElement owner, string key, string fallback)
        {
            var resourceValue = owner?.TryFindResource(key) as string;
            if (!string.IsNullOrWhiteSpace(resourceValue))
            {
                return resourceValue;
            }

            var value = ResourceProvider.GetString(key);
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }

        private static AchievementOverridesService CurrentOverridesService =>
            PlayniteAchievementsPlugin.Instance?.AchievementOverridesService;

        private static PlayniteAchievements.Models.Settings.PersistedSettings CurrentSettings =>
            PlayniteAchievementsPlugin.Instance?.Settings?.Persisted;

        private static GameCustomDataStore CurrentStore =>
            PlayniteAchievementsPlugin.Instance?.GameCustomDataStore;

        private sealed class AchievementRowContext
        {
            private AchievementDisplayItem _displayItem;
            private RecentAchievementItem _recentItem;

            public Guid GameId { get; private set; }

            public string ApiName { get; private set; }

            public string DisplayName { get; private set; }

            public string DisplayIcon { get; private set; }

            public bool IsCapstone { get; private set; }

            public string CategoryLabel { get; private set; }

            public string CategoryType { get; private set; }

            public static bool TryCreate(object data, out AchievementRowContext context)
            {
                context = null;
                if (data is AchievementDisplayItem displayItem &&
                    displayItem.PlayniteGameId.HasValue)
                {
                    var apiName = (displayItem.ApiName ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(apiName))
                    {
                        return false;
                    }

                    context = new AchievementRowContext
                    {
                        _displayItem = displayItem,
                        GameId = displayItem.PlayniteGameId.Value,
                        ApiName = apiName,
                        DisplayName = displayItem.DisplayNameResolved,
                        DisplayIcon = displayItem.DisplayIcon,
                        IsCapstone = displayItem.Source?.IsCapstone == true,
                        CategoryLabel = displayItem.CategoryLabel,
                        CategoryType = displayItem.CategoryType
                    };
                    return true;
                }

                if (data is RecentAchievementItem recentItem &&
                    recentItem.PlayniteGameId.HasValue)
                {
                    var apiName = (recentItem.ApiName ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(apiName))
                    {
                        return false;
                    }

                    context = new AchievementRowContext
                    {
                        _recentItem = recentItem,
                        GameId = recentItem.PlayniteGameId.Value,
                        ApiName = apiName,
                        DisplayName = recentItem.Name,
                        DisplayIcon = recentItem.DisplayIcon,
                        IsCapstone = false,
                        CategoryLabel = recentItem.CategoryLabel,
                        CategoryType = recentItem.CategoryType
                    };
                    return true;
                }

                return false;
            }

            public void ApplyCategoryLabel(string value)
            {
                CategoryLabel = value;
                if (_displayItem != null)
                {
                    _displayItem.CategoryLabel = value;
                }

                if (_recentItem != null)
                {
                    _recentItem.CategoryLabel = value;
                }
            }

            public void ApplyCategoryType(string value)
            {
                CategoryType = value;
                if (_displayItem != null)
                {
                    _displayItem.CategoryType = value;
                }

                if (_recentItem != null)
                {
                    _recentItem.CategoryType = value;
                }
            }

            public void ApplyNote(string value)
            {
                if (_displayItem != null)
                {
                    _displayItem.AchievementNote = value;
                }

                if (_recentItem != null)
                {
                    _recentItem.AchievementNote = value;
                }
            }
        }
    }
}
