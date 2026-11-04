using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Playnite.SDK;
using PlayniteAchievements.Services.Achievements;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>
    /// The per-category actions a grid's category row menu offers, the same set the Manage
    /// Achievements Categories tab has in its row menu: rename, make subcategory of, make top
    /// level, merge into, duplicate, delete. Every action runs through <see cref="CategoryEditor"/>
    /// over one snapshot taken when the menu opened, so the targets offered and the guards
    /// applied match the tab's exactly.
    /// </summary>
    internal static class CategoryRowActionsMenuBuilder
    {
        /// <summary>
        /// Appends the actions for <paramref name="categoryLabel"/>. Nothing is appended for the
        /// Default bucket, which cannot move, merge, duplicate or be deleted. Returns true when
        /// any item was appended.
        /// </summary>
        /// <param name="onApplied">Runs after a write landed, with its outcome.</param>
        public static bool Append(
            ContextMenu menu,
            FrameworkElement owner,
            CategoryEditor editor,
            CategoryEditSnapshot snapshot,
            string categoryLabel,
            Action<CategoryEditOutcome> onApplied)
        {
            var label = CategoryPathHelper.NormalizePath(categoryLabel);
            if (menu == null || editor == null || snapshot == null ||
                string.IsNullOrWhiteSpace(label) || CategoryEditSnapshot.IsDefaultLabel(label))
            {
                return false;
            }

            var labels = new[] { label };

            void Run(Func<CategoryEditOutcome> action)
            {
                var outcome = action();
                if (outcome?.Applied == true)
                {
                    onApplied?.Invoke(outcome);
                }
            }

            menu.Items.Add(GameRowContextMenuBuilder.CreateMenuItem(
                owner,
                "LOCPlayAch_Common_Rename",
                () =>
                {
                    var leaf = PromptForLeafName(owner, CategoryPathHelper.GetLeafName(label));
                    if (leaf == null)
                    {
                        return;
                    }

                    Run(() => editor.RenameCategoryLabel(
                        snapshot,
                        label,
                        CategoryPathHelper.Join(CategoryPathHelper.GetParentPath(label), leaf)));
                }));

            var subcategoryMenu = new MenuItem
            {
                Header = L(owner, "LOCPlayAch_ManageAchievements_Category_Context_MakeSubcategoryOf")
            };
            var validTargets = snapshot.MovableLabels
                .Where(candidate => CategoryEditor.CanNestCategoryRowsUnder(snapshot, labels, candidate))
                .ToList();
            var targetRows = CategoryFilterMenuBuilder.BuildRows(validTargets);
            var itemStyle = CategoryFilterMenuBuilder.ResolveActionItemStyle(owner);
            foreach (var targetRow in targetRows)
            {
                subcategoryMenu.Items.Add(CategoryFilterMenuBuilder.CreateActionItem(
                    targetRow,
                    itemStyle,
                    target => Run(() => editor.NestCategoryRowsUnder(snapshot, labels, target))));
            }

            subcategoryMenu.IsEnabled = targetRows.Count > 0;
            menu.Items.Add(subcategoryMenu);

            var makeTopLevelItem = GameRowContextMenuBuilder.CreateMenuItem(
                owner,
                "LOCPlayAch_ManageAchievements_Category_Context_MakeTopLevel",
                () => Run(() => editor.NestCategoryRowsUnder(snapshot, labels, null)));
            makeTopLevelItem.IsEnabled = CategoryPathHelper.GetDepth(label) > 1;
            menu.Items.Add(makeTopLevelItem);

            menu.Items.Add(new Separator());

            // Merge is one source into one target. The target list is the same connector tree
            // the subcategory submenu draws; the source's own subtree is excluded because the
            // merge folds that whole subtree away, and Default stays as a way to un-categorize.
            var mergeMenu = new MenuItem
            {
                Header = L(owner, "LOCPlayAch_ManageAchievements_Category_MergeDialog_Target"),
                ToolTip = L(owner, "LOCPlayAch_ManageAchievements_Category_MergeTooltip")
            };
            var mergeTargets = snapshot.NonBlankLabels
                .Where(candidate => !CategoryPathHelper.IsSelfOrDescendantOf(candidate, label))
                .ToList();
            var mergeTargetRows = CategoryFilterMenuBuilder.BuildRows(mergeTargets);
            var mergeStyle = CategoryFilterMenuBuilder.ResolveActionItemStyle(owner);
            foreach (var mergeTargetRow in mergeTargetRows)
            {
                mergeMenu.Items.Add(CategoryFilterMenuBuilder.CreateActionItem(
                    mergeTargetRow,
                    mergeStyle,
                    target => Run(() => editor.MergeCategoryInto(snapshot, label, target))));
            }

            mergeMenu.IsEnabled = mergeMenu.Items.Count > 0;
            menu.Items.Add(mergeMenu);

            menu.Items.Add(GameRowContextMenuBuilder.CreateMenuItem(
                owner,
                "LOCPlayAch_Common_Duplicate",
                () => Run(() => editor.DuplicateCategories(snapshot, labels))));

            menu.Items.Add(GameRowContextMenuBuilder.CreateMenuItem(
                owner,
                "LOCPlayAch_Common_Delete",
                () =>
                {
                    // Deleting an empty branch is quiet; deleting one that holds achievements
                    // asks first, because those achievements fall back to the Default bucket.
                    var affected = CategoryEditor.CountAchievementsInCategories(snapshot, labels);
                    if (affected > 0)
                    {
                        var result = API.Instance?.Dialogs?.ShowMessage(
                            string.Format(
                                L(owner, "LOCPlayAch_ManageAchievements_Category_DeleteConfirmSingle"),
                                CategoryPathHelper.ToDisplayPath(label),
                                affected),
                            L(owner, "LOCPlayAch_Title_PluginName"),
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Warning) ?? MessageBoxResult.None;
                        if (result != MessageBoxResult.Yes)
                        {
                            return;
                        }
                    }

                    Run(() => editor.DeleteCategories(snapshot, labels));
                }));

            return true;
        }

        /// <summary>
        /// Asks for a new leaf name. Null when cancelled or blank; a typed path separator is
        /// refused, since nesting is expressed by the subcategory actions rather than by spelling
        /// out a path.
        /// </summary>
        private static string PromptForLeafName(FrameworkElement owner, string currentLeaf)
        {
            var result = API.Instance?.Dialogs?.SelectString(
                L(owner, "LOCPlayAch_Common_Name"),
                L(owner, "LOCPlayAch_Common_Rename"),
                currentLeaf ?? string.Empty);
            if (result?.Result != true)
            {
                return null;
            }

            var typed = (result.SelectedString ?? string.Empty).Trim();
            if (typed.Length == 0)
            {
                return null;
            }

            if (CategoryPathHelper.ContainsSeparator(typed))
            {
                API.Instance?.Dialogs?.ShowMessage(
                    L(owner, "LOCPlayAch_ManageAchievements_Category_PathSeparatorNotAllowed"),
                    L(owner, "LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return null;
            }

            return CategoryPathHelper.SanitizeSegment(typed);
        }

        private static string L(FrameworkElement owner, string key)
        {
            return owner?.TryFindResource(key) as string ?? ResourceProvider.GetString(key);
        }
    }
}
