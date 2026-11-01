using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Playnite.SDK;
using PlayniteAchievements.Services;
using PlayniteAchievements.Providers.Manual;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Showcase;
using PlayniteAchievements.Services.UI;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;
using PlayniteAchievements.ViewModels.ManageAchievements;
using PlayniteAchievements.Views;
using PlayniteAchievements.Views.Dialogs;

namespace PlayniteAchievements.Views.Helpers
{
    internal static class AchievementRowOptionsMenuBuilder
    {
        /// <param name="onGoalChanged">
        /// Optional cheap re-sort for a goal toggle. Toggling a goal only changes ordering, so a
        /// surface that can re-sort its existing rows should do that instead of paying for
        /// <paramref name="onChanged"/>, which reloads the game's achievements from cache and
        /// rebuilds every row. Return false to fall back to the full reload.
        /// </param>
        /// <param name="onCapstoneChanged">
        /// Optional cheap re-stamp for setting a capstone, given the new capstone's ApiName. Only
        /// called when a capstone is being set: clearing one has to restore provider-assigned
        /// capstones, which only hydration knows, so that case still takes
        /// <paramref name="onChanged"/>. Return false to fall back to the full reload.
        /// </param>
        public static bool AppendAchievementOptions(
            ContextMenu menu,
            object data,
            FrameworkElement resourceOwner,
            Action onChanged,
            DependencyObject menuSource,
            bool includeViewCaptures = false,
            Func<bool> onGoalChanged = null,
            Func<string, bool> onCapstoneChanged = null)
        {
            if (menu == null || !AchievementRowContext.TryCreate(data, out var context))
            {
                return false;
            }

            // User-earned achievements only (opted in by the caller); disabled when the achievement
            // has no saved captures. Friend achievement rows never opt in.
            if (includeViewCaptures && data is AchievementDisplayItem achItem && !(data is FriendAchievementDisplayItem))
            {
                // Live presence check (cached per game) so this works in every user-scope grid,
                // including theme grids whose rows aren't presence-marked.
                var hasCaptures = PlayniteAchievementsPlugin.Instance?.CaptureLibraryService?
                    .AchievementHasCaptures(achItem.GameName, achItem.DisplayName) == true;
                var captureItem = new MenuItem
                {
                    Header = L(resourceOwner, "LOCPlayAch_Column_Captures"),
                    IsEnabled = hasCaptures
                };
                captureItem.Click += (_, __) => PlayniteAchievementsPlugin.Instance?.OpenCapturesViewer(achItem);
                menu.Items.Add(captureItem);
            }

            AppendShowcasePinItem(menu, data, resourceOwner);
            // A friend's row describes their progress, not the user's, so its goal/capstone state
            // would drive the toggle in the wrong direction. Theme grids can be pointed at friend
            // collections, so gate here rather than relying on the caller.
            if (!(data is FriendAchievementDisplayItem))
            {
                if (CanUserUnlock(context))
                {
                    menu.Items.Add(CreateUnlockItem(context, resourceOwner, onChanged));
                }

                menu.Items.Add(CreateSetGoalItem(context, resourceOwner, onChanged, onGoalChanged));
                menu.Items.Add(CreateSetCapstoneItem(context, resourceOwner, onChanged, onCapstoneChanged));
            }

            menu.Items.Add(CreateCategoryMenu(context, resourceOwner, onChanged));
            menu.Items.Add(CreateTagsMenu(context, resourceOwner, onChanged));
            menu.Items.Add(CreateFiltersMenu(context, resourceOwner, onChanged));
            menu.Items.Add(CreateNotesMenu(context, resourceOwner, onChanged));

            // Required rather than optional: a call site that forgets the row would lose the
            // display settings entry silently, so the compiler asks for it.
            GridDisplaySettingsMenuBuilder.Append(menu, resourceOwner, menuSource);
            return true;
        }

        private static void AppendShowcasePinItem(
            ContextMenu menu,
            object data,
            FrameworkElement resourceOwner)
        {
            if (!ShowcasePinService.TryGetAchievementIdentity(
                    data,
                    out var gameId,
                    out var apiName,
                    out var gameName,
                    out var achievementName,
                    out var friendOwned) ||
                friendOwned)
            {
                return;
            }

            ShowcasePinMenuBuilder.AppendAchievementMenu(
                menu,
                resourceOwner,
                gameId,
                apiName,
                gameName,
                achievementName);
        }

        /// <summary>
        /// "Unlock", offered only for a locked achievement whose unlock state the user owns: one
        /// they authored, or any achievement of a manually tracked game.
        /// </summary>
        /// <remarks>
        /// The item is built only when the write would be accepted, and the service refuses a
        /// provider achievement regardless, so a real provider's unlock counts can never be moved
        /// from a context menu. An authored achievement re-projects through the custom-data change;
        /// a manual one needs its link applied onto the cached game, which is what the applier does.
        /// </remarks>
        private static MenuItem CreateUnlockItem(
            AchievementRowContext context,
            FrameworkElement resourceOwner,
            Action onChanged)
        {
            var item = new MenuItem
            {
                Header = L(resourceOwner, "LOCPlayAch_Common_Unlocked")
            };

            item.Click += (_, __) =>
            {
                var plugin = PlayniteAchievementsPlugin.Instance;
                var overrides = plugin?.AchievementOverridesService;
                if (overrides == null)
                {
                    return;
                }

                var result = overrides.TryUnlockUserOwnedAchievement(context.GameId, context.ApiName);
                if (result == ManualUnlockWriteResult.Manual &&
                    ManualAchievementsProvider.TryGetManualLink(context.GameId, out var link) &&
                    link != null)
                {
                    // No manual source to hand here; the applier keeps the platform the cache
                    // already resolved rather than blanking it, and still re-applies the unlock.
                    new ManualLinkCacheApplier(
                        plugin.AchievementDataService,
                        plugin.CacheManager,
                        plugin.Settings).Apply(context.GameId, link, source: null);
                }

                if (result == ManualUnlockWriteResult.Custom || result == ManualUnlockWriteResult.Manual)
                {
                    onChanged?.Invoke();
                }
            };

            return item;
        }

        /// <summary>
        /// Whether this row's unlock state is the user's to set: a locked achievement they authored,
        /// or a locked one on a manually tracked game.
        /// </summary>
        private static bool CanUserUnlock(AchievementRowContext context)
        {
            if (context == null)
            {
                return false;
            }

            return UserOwnedUnlockRules.CanUserUnlock(
                context.Unlocked,
                CustomAchievementProjectionService.IsCustomApiName(context.ApiName),
                ManualAchievementsProvider.TryGetManualLink(context.GameId, out var link) && link != null);
        }

        private static MenuItem CreateSetGoalItem(
            AchievementRowContext context,
            FrameworkElement resourceOwner,
            Action onChanged,
            Func<bool> onGoalChanged)
        {
            // A goal is something still to be earned, and unlocking one retires it, so offering
            // the toggle on an unlocked row would be a dead end.
            var item = new MenuItem
            {
                Header = L(resourceOwner, "LOCPlayAch_Menu_SetGoal"),
                IsCheckable = true,
                IsChecked = context.IsGoal,
                IsEnabled = !context.Unlocked
            };
            item.Click += (_, __) =>
            {
                // The write returns the new goal position, so the row can be stamped without a
                // second read. The surface then re-sorts in the same pass, landing the accent and
                // the new position in one frame.
                var result = CurrentMarkerToggle?.ToggleGoal(context.ToMarkerTarget())
                    ?? default(AchievementMarkerToggle.GoalToggleResult);
                if (!result.Attempted)
                {
                    return;
                }

                context.ApplyGoal(result.IsGoal, result.GoalOrderIndex);

                if (onGoalChanged?.Invoke() == true)
                {
                    return;
                }

                onChanged?.Invoke();
            };

            return item;
        }

        /// <summary>What the click would do, worded as the Editor's capstone button: Add, Replace or Remove.</summary>
        private static string BuildCapstoneActionLabel(
            FrameworkElement resourceOwner,
            AchievementMarkerToggle.CapstoneAction action)
        {
            switch (action)
            {
                case AchievementMarkerToggle.CapstoneAction.Remove:
                    return L(resourceOwner, "LOCPlayAch_Common_Remove");
                case AchievementMarkerToggle.CapstoneAction.Replace:
                    return L(resourceOwner, "LOCPlayAch_Button_Replace");
                default:
                    return L(resourceOwner, "LOCPlayAch_Common_Add");
            }
        }

        private static MenuItem CreateSetCapstoneItem(
            AchievementRowContext context,
            FrameworkElement resourceOwner,
            Action onChanged,
            Func<string, bool> onCapstoneChanged)
        {
            // Capstone > Add, Replace or Remove, as the Editor offers it: the entry says what the
            // click will do, and a replacement names the capstone it displaces.
            var action = AchievementMarkerToggle.CapstoneAction.Add;
            string displaced = null;
            if (CurrentMarkerToggle != null)
            {
                action = CurrentMarkerToggle.ResolveCapstoneAction(context.ToMarkerTarget(), out displaced);
            }

            var parent = new MenuItem { Header = L(resourceOwner, "LOCPlayAch_Dynamic_Capstone") };
            var item = new MenuItem
            {
                Header = BuildCapstoneActionLabel(resourceOwner, action),
                ToolTip = action == AchievementMarkerToggle.CapstoneAction.Replace && !string.IsNullOrWhiteSpace(displaced)
                    ? string.Format(L(resourceOwner, "LOCPlayAch_Capstone_Replaces"), displaced)
                    : null
            };
            parent.Items.Add(item);
            item.Click += async (_, __) =>
            {
                var toggle = CurrentMarkerToggle;
                if (toggle == null)
                {
                    return;
                }

                var result = await toggle.ToggleCapstoneAsync(context.ToMarkerTarget());
                if (!result.Attempted)
                {
                    return;
                }

                if (!result.Success)
                {
                    ShowError(result.ErrorMessage);
                    return;
                }

                // Settled from the stored set rather than from this one result: a game carries
                // several capstones, so the write says nothing about the other rows. Doing it here
                // rather than leaving it to a reload is what makes the glyph change on the click
                // that caused it, instead of on the one after.
                if (onCapstoneChanged?.Invoke(result.CapstoneApiName) == true)
                {
                    return;
                }

                onChanged?.Invoke();
            };

            return parent;
        }

        /// <summary>
        /// The game's categories, the achievement's ticked: picking one files it there, unticking it
        /// takes it out, and the search creates one. The same list the editor's Category menu has.
        /// </summary>
        private static MenuItem CreateCategoryMenu(
            AchievementRowContext context,
            FrameworkElement resourceOwner,
            Action onChanged)
        {
            var menu = new MenuItem
            {
                Header = L(resourceOwner, "LOCPlayAch_Common_Category")
            };

            foreach (var option in CategoryPickerResolver.BuildOptions(ResolveGameCategoryLabels(context.GameId)))
            {
                var label = option.Label;
                if (string.IsNullOrWhiteSpace(label))
                {
                    continue;
                }

                var categoryItem = new MenuItem
                {
                    Header = option.LeafDisplay,
                    ToolTip = option.PathDisplay,
                    Tag = label,
                    IsCheckable = true,
                    IsChecked = CategoryPathHelper.IsSame(context.CategoryLabel, label)
                };
                // The click has already flipped the tick.
                categoryItem.Click += (_, __) =>
                {
                    if (SetCategory(context, categoryItem.IsChecked ? label : null))
                    {
                        onChanged?.Invoke();
                    }
                };
                menu.Items.Add(categoryItem);
            }

            CategoryMenuSearch.Attach(menu, name =>
            {
                if (CategoryCreationPrompt.TryValidate(name, out var leafName) &&
                    SetCategory(context, CategoryPathHelper.SanitizeSegment(leafName)))
                {
                    onChanged?.Invoke();
                }
            });

            return menu;
        }

        /// <summary>The tags available to the game, the achievement's ticked; the search creates one.</summary>
        private static MenuItem CreateTagsMenu(
            AchievementRowContext context,
            FrameworkElement resourceOwner,
            Action onChanged)
        {
            var typesMenu = new MenuItem
            {
                Header = L(resourceOwner, "LOCPlayAch_Common_Tags")
            };
            var effectiveTypes = AchievementTagHelper.Parse(context.Tags);
            var tags = AchievementTagRegistry.GetShownKeys(context.GameId).ToList();
            var iconKeys = SelectorContextMenuHelper.TagIconKeys(tags, context.GameId);
            for (var i = 0; i < tags.Count; i++)
            {
                var capturedType = tags[i];
                var typeItem = new MenuItem
                {
                    IsCheckable = true,
                    StaysOpenOnClick = true,
                    IsChecked = effectiveTypes.Any(value =>
                        string.Equals(value, capturedType, StringComparison.OrdinalIgnoreCase))
                };
                SelectorContextMenuHelper.SetTagHeader(
                    typeItem, iconKeys, i, AchievementTagRegistry.GetName(capturedType, context.GameId));
                typeItem.Click += (_, __) =>
                {
                    if (typeItem.IsChecked)
                    {
                        AddTag(context, capturedType);
                    }
                    else
                    {
                        RemoveTag(context, capturedType);
                    }

                    onChanged?.Invoke();
                };
                typesMenu.Items.Add(typeItem);
            }

            SelectorSearch.SetCreateAction(typesMenu, name =>
            {
                var key = CurrentOverridesService?.CreateGameTag(context.GameId, name);
                if (string.IsNullOrWhiteSpace(key))
                {
                    return;
                }

                AddTag(context, key);
                onChanged?.Invoke();
            });
            return typesMenu;
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
                Header = L(resourceOwner, "LOCPlayAch_Menu_Filters")
            };

            var filterOutItem = new MenuItem
            {
                Header = L(resourceOwner, "LOCPlayAch_ManageAchievements_Filters_FilterOut"),
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
                Header = L(resourceOwner, "LOCPlayAch_ManageAchievements_Filters_FilterOutOfSummaries"),
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
                Header = L(resourceOwner, "LOCPlayAch_Common_Notes")
            };

            var viewItem = CreateMenuItem(
                L(resourceOwner, "LOCPlayAch_Common_View"),
                () => OpenNoteDialog(context, note, isEditMode: false, resourceOwner, onChanged));
            viewItem.IsEnabled = hasNote;
            menu.Items.Add(viewItem);

            menu.Items.Add(CreateMenuItem(
                L(resourceOwner, "LOCPlayAch_Common_Edit"),
                () => OpenNoteDialog(context, note, isEditMode: true, resourceOwner, onChanged)));

            return menu;
        }

        private static void AddTag(AchievementRowContext context, string tag)
        {
            var map = GameCustomDataLookup.GetAchievementTagOverrides(
                context.GameId,
                CurrentSettings,
                CurrentStore);
            var normalizedMap = new Dictionary<string, string>(map, StringComparer.OrdinalIgnoreCase);
            var currentEffective = AchievementTagHelper.Normalize(context.Tags);
            var merged = AchievementTagHelper.WithTag(currentEffective, tag, include: true);
            if (string.Equals(merged, currentEffective, StringComparison.Ordinal))
            {
                return;
            }

            // "" when nothing is left: the user removed every tag.
            normalizedMap[context.ApiName] = AchievementTagHelper.StripDerived(merged) ?? string.Empty;
            CurrentOverridesService?.SetAchievementTagOverrides(context.GameId, normalizedMap);
            NotifySummaryRowsChanged(context.GameId);
            context.ApplyTags(merged);
        }

        private static void RemoveTag(AchievementRowContext context, string tag)
        {
            var map = GameCustomDataLookup.GetAchievementTagOverrides(
                context.GameId,
                CurrentSettings,
                CurrentStore);
            var normalizedMap = new Dictionary<string, string>(map, StringComparer.OrdinalIgnoreCase);
            var currentEffective = AchievementTagHelper.Normalize(context.Tags);
            var remaining = AchievementTagHelper.WithTag(currentEffective, tag, include: false);
            if (string.Equals(remaining, currentEffective, StringComparison.Ordinal))
            {
                return;
            }

            // "" when nothing is left: the user removed every tag.
            normalizedMap[context.ApiName] = AchievementTagHelper.StripDerived(remaining) ?? string.Empty;
            CurrentOverridesService?.SetAchievementTagOverrides(context.GameId, normalizedMap);
            NotifySummaryRowsChanged(context.GameId);
            context.ApplyTags(remaining);
        }

        /// <summary>
        /// Every category the game currently has, in tree order, for the Set Category Label picker.
        ///
        /// Read from the achievement data rather than rebuilt from the category override maps: the
        /// overrides only hold categories somebody has already edited, so a picker built from them
        /// would omit every provider-supplied category - normally the whole list. The stored
        /// category metadata is added through the same builder the Manage pickers use, so a
        /// category created empty is offered here too. One cached single-game read, on a menu click.
        /// </summary>
        private static IReadOnlyList<string> ResolveGameCategoryLabels(Guid gameId)
        {
            var achievements = PlayniteAchievementsPlugin.Instance?
                .AchievementDataService?
                .GetGameAchievementData(gameId)?
                .Achievements;
            if (achievements == null)
            {
                return Array.Empty<string>();
            }

            var resolved = GameCustomDataLookup.ResolveGameCustomData(gameId, CurrentSettings, CurrentStore);
            return CategoryPickerResolver.BuildGameCategoryLabels(
                achievements.Select(achievement =>
                    AchievementCategoryHelper.NormalizeCategoryOrDefault(achievement?.Category)),
                resolved?.AchievementCategoryOrder,
                resolved?.AchievementCategoryImageOverrides?.Keys,
                resolved?.GameSummaryCategory?.Label);
        }

        /// <summary>
        /// Files the achievement under <paramref name="label"/>, or, for null, drops its category
        /// override so the provider's category applies again.
        /// </summary>
        private static bool SetCategory(AchievementRowContext context, string label)
        {
            var normalizedCategory = AchievementCategoryHelper.NormalizeCategory(label);
            var map = GameCustomDataLookup.GetAchievementCategoryOverrides(
                context.GameId,
                CurrentSettings,
                CurrentStore);
            var normalizedMap = CloneStringMap(map);
            if (string.IsNullOrWhiteSpace(normalizedCategory))
            {
                if (!normalizedMap.Remove(context.ApiName))
                {
                    return false;
                }
            }
            else
            {
                normalizedMap[context.ApiName] = normalizedCategory;
            }

            MaintainIfRescoped(
                context.GameId,
                CurrentOverridesService?.SetAchievementCategoryOverrides(context.GameId, normalizedMap) == true);
            NotifySummaryRowsChanged(context.GameId);
            if (!string.IsNullOrWhiteSpace(normalizedCategory))
            {
                context.ApplyCategoryLabel(normalizedCategory);
            }

            return true;
        }

        /// <summary>
        /// Brings a moved auto capstone's unlock into step with what it now stands for, since no
        /// refresh follows a hand edit. Quiet: the user moved it, the game reported nothing.
        /// </summary>
        private static void MaintainIfRescoped(Guid gameId, bool rescoped)
        {
            if (rescoped)
            {
                PlayniteAchievementsPlugin.Instance?.AutoCapstoneMaintainer?.Maintain(
                    gameId,
                    announceUnlocks: false);
            }
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
                ? L(resourceOwner, "LOCPlayAch_NotesDialog_EditTitle")
                : L(resourceOwner, "LOCPlayAch_NotesDialog_ViewTitle");
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

            WindowPlacementPersistenceService.Attach(
                window,
                isEditMode ? "AchievementNoteEdit" : "AchievementNoteView");
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
            NotifySummaryRowsChanged(context.GameId);
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

        private static string L(FrameworkElement owner, string key)
        {
            var resourceValue = owner?.TryFindResource(key) as string;
            if (!string.IsNullOrWhiteSpace(resourceValue))
            {
                return resourceValue;
            }

            return ResourceProvider.GetString(key);
        }

        // Category, category-type and note writes stay out of the store's summary flag, which would
        // undo the projection's deferred warm (see ManageCustomDataInvalidationDefinitionTests), so
        // they repaint only the clicked row. The Manage editor raises this same scoped invalidation
        // for its edits; without it the summary memo, the Overview's per-game delta, the Showcase
        // and the start page all kept showing the old value.
        private static void NotifySummaryRowsChanged(Guid gameId)
        {
            if (gameId != Guid.Empty)
            {
                PlayniteAchievementsPlugin.Instance?.CacheManager?.NotifyCacheInvalidated(new[] { gameId });

                // These writes do not affect summary data, so the store's own notification skips
                // the theme's library-wide lists, which carry this game's labels and notes as
                // well. The same call the Manage window makes after its edits; it logs its own
                // failures.
                PlayniteAchievementsPlugin.Instance?.ThemeIntegrationService?.NotifyCustomDataChanged(gameId);
            }
        }
        private static AchievementOverridesService CurrentOverridesService =>
            PlayniteAchievementsPlugin.Instance?.AchievementOverridesService;

        private static AchievementMarkerToggle CurrentMarkerToggle =>
            PlayniteAchievementsPlugin.Instance?.AchievementMarkerToggle;

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

            public bool IsGoal { get; private set; }

            public bool Unlocked { get; private set; }

            public string CategoryLabel { get; private set; }

            public string Tags { get; private set; }

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
                        IsGoal = displayItem.IsGoal,
                        Unlocked = displayItem.Unlocked,
                        CategoryLabel = displayItem.CategoryLabel,
                        Tags = displayItem.Tags
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
                        // Recent rows are unlocks by definition, so they are never goals.
                        IsGoal = false,
                        Unlocked = true,
                        CategoryLabel = recentItem.CategoryLabel,
                        Tags = recentItem.Tags
                    };
                    return true;
                }

                return false;
            }

            public AchievementMarkerTarget ToMarkerTarget() =>
                new AchievementMarkerTarget(GameId, ApiName, IsCapstone, IsGoal, Unlocked);

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

            public void ApplyTags(string value)
            {
                Tags = value;
                if (_displayItem != null)
                {
                    _displayItem.Tags = value;
                }

                if (_recentItem != null)
                {
                    _recentItem.Tags = value;
                }
            }

            public void ApplyGoal(bool isGoal, int goalOrderIndex)
            {
                IsGoal = isGoal;
                if (_displayItem != null)
                {
                    _displayItem.IsGoal = isGoal;
                    _displayItem.GoalOrderIndex = goalOrderIndex;
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
