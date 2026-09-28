using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Tests.Views
{
    [TestClass]
    public class DataGridColumnLayoutServiceTests
    {
        [TestMethod]
        public void Attach_WithEmptyWidthsUsesStarFallbackUntilMeasuredWithoutPersisting()
        {
            RunOnStaThread(() =>
            {
                var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var saveCount = 0;
                var grid = CreateGrid();
                var service = CreateService(grid, order, () => saveCount++);

                service.Attach();

                foreach (var column in grid.Columns)
                {
                    Assert.IsTrue(column.Width.IsStar);
                    Assert.AreEqual(1d, column.Width.Value);
                }
                Assert.AreEqual(0, saveCount);

                service.Detach();
            });
        }

        [TestMethod]
        public void WidthChange_DoesNotPersistColumnOrder()
        {
            RunOnStaThread(() =>
            {
                var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var saveCount = 0;
                var grid = CreateGrid();
                var service = CreateService(grid, order, () => saveCount++);

                service.Attach();
                grid.Columns[1].Width = new DataGridLength(175, DataGridLengthUnitType.Pixel);
                DrainDispatcher();
                service.Detach();

                Assert.AreEqual(0, order.Count);
                Assert.AreEqual(0, saveCount);
            });
        }

        [TestMethod]
        public void ForcedCollapsedColumn_DoesNotLoseWidthWhenVisibleAgain()
        {
            RunOnStaThread(() =>
            {
                var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var widths = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
                {
                    ["A"] = 40,
                    ["B"] = 120,
                    ["C"] = 120
                };
                var visibility = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
                {
                    ["A"] = true,
                    ["B"] = true,
                    ["C"] = true
                };
                var grid = CreateGrid();
                grid.Columns[0].MinWidth = 28;
                grid.Columns[0].MaxWidth = 96;
                grid.Columns[0].Width = new DataGridLength(40, DataGridLengthUnitType.Pixel);
                var service = CreateService(grid, order, () => { }, widths, initialVisibility: visibility);

                service.ForcedCollapsedKeys.Add("A");
                service.Attach();

                Assert.AreEqual(Visibility.Collapsed, grid.Columns[0].Visibility);
                Assert.AreEqual(28d, grid.Columns[0].MinWidth);
                Assert.AreEqual(96d, grid.Columns[0].MaxWidth);
                Assert.AreEqual(40d, grid.Columns[0].Width.Value);

                service.ForcedCollapsedKeys.Remove("A");
                service.Refresh();

                Assert.AreEqual(Visibility.Visible, grid.Columns[0].Visibility);
                Assert.AreEqual(28d, grid.Columns[0].MinWidth);
                Assert.AreEqual(96d, grid.Columns[0].MaxWidth);
                Assert.AreEqual(40d, grid.Columns[0].Width.Value);

                service.Detach();
            });
        }

        [TestMethod]
        public void Attach_WithDelayedInitialRenderRestoresGridAfterSuccessfulNormalization()
        {
            RunOnStaThread(() =>
            {
                var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var saveCount = 0;
                var grid = CreateGrid();
                grid.Width = 360;
                grid.Height = 160;
                grid.Opacity = 0.42d;
                grid.IsHitTestVisible = true;

                var window = new Window
                {
                    Width = 420,
                    Height = 220,
                    Content = grid,
                    ShowActivated = false
                };

                DataGridColumnLayoutService service = null;
                try
                {
                    window.Show();
                    grid.UpdateLayout();

                    service = CreateService(grid, order, () => saveCount++);
                    service.DelayInitialRenderUntilNormalized = true;
                    service.Attach();
                    DrainDispatcher();

                    Assert.AreEqual(0.42d, grid.Opacity);
                    Assert.IsTrue(grid.IsHitTestVisible);
                    foreach (var column in grid.Columns)
                    {
                        Assert.IsTrue(column.Width.IsAbsolute);
                    }
                    Assert.AreEqual(0, saveCount);
                }
                finally
                {
                    service?.Detach();
                    window.Close();
                }
            });
        }

        [TestMethod]
        public void Attach_WithDelayedInitialRenderRetriesFailedNormalizationWithoutSaving()
        {
            RunOnStaThread(() =>
            {
                var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var saveCount = 0;
                var grid = CreateGrid();
                grid.Opacity = 0.75d;
                var service = CreateService(grid, order, () => saveCount++);
                service.DelayInitialRenderUntilNormalized = true;

                service.Attach();

                Assert.AreEqual(0d, grid.Opacity);
                DrainDispatcher();

                Assert.AreEqual(0, saveCount);

                service.Detach();
                Assert.AreEqual(0.75d, grid.Opacity);
            });
        }

        [TestMethod]
        public void Attach_WithEmptyWidthsUsesDefaultSeedProportionsWithoutPersisting()
        {
            RunOnStaThread(() =>
            {
                var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var defaultSeeds = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
                {
                    ["A"] = 50,
                    ["B"] = 300,
                    ["C"] = 50
                };
                var saveCount = 0;
                var grid = CreateGrid();
                grid.Width = 600;
                grid.Height = 160;

                var window = new Window
                {
                    Width = 640,
                    Height = 220,
                    Content = grid,
                    ShowActivated = false
                };

                DataGridColumnLayoutService service = null;
                try
                {
                    window.Show();
                    grid.UpdateLayout();

                    service = CreateService(grid, order, () => saveCount++, defaultWidthSeeds: defaultSeeds);
                    service.Attach();
                    DrainDispatcher();

                    Assert.IsTrue(grid.Columns[1].Width.DisplayValue > grid.Columns[0].Width.DisplayValue * 3);
                    Assert.IsTrue(grid.Columns[1].Width.DisplayValue > grid.Columns[2].Width.DisplayValue * 3);
                    Assert.AreEqual(0, saveCount);
                }
                finally
                {
                    service?.Detach();
                    window.Close();
                }
            });
        }

        [TestMethod]
        public void Detach_ClearsInitialRetryAndScrollViewerHandlers()
        {
            RunOnStaThread(() =>
            {
                var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var grid = CreateGrid();
                grid.Width = 360;
                grid.Height = 160;

                var window = new Window
                {
                    Width = 420,
                    Height = 220,
                    Content = grid,
                    ShowActivated = false
                };

                DataGridColumnLayoutService service = null;
                try
                {
                    window.Show();
                    grid.UpdateLayout();

                    service = CreateService(grid, order, () => { });
                    service.DelayInitialRenderUntilNormalized = true;
                    service.Attach();
                    DrainDispatcher();

                    Assert.IsNotNull(GetPrivateField<ScrollViewer>(service, "_normalizationScrollViewer"));

                    service.Detach();

                    Assert.IsNull(GetPrivateField<ScrollViewer>(service, "_normalizationScrollViewer"));
                    Assert.IsFalse(GetPrivateField<bool>(service, "_normalizationQueued"));
                    Assert.IsFalse(GetPrivateField<bool>(service, "_scrollViewerAttachQueued"));
                    Assert.IsFalse(GetPrivateField<bool>(service, "_isInitialRenderSuppressed"));
                }
                finally
                {
                    service?.Detach();
                    window.Close();
                }
            });
        }

        [TestMethod]
        public void ExplicitColumnReorder_PersistsCurrentDisplayIndexes()
        {
            RunOnStaThread(() =>
            {
                var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var saveCount = 0;
                var grid = CreateGrid();
                var service = CreateService(grid, order, () => saveCount++);
                service.Attach();

                grid.Columns[2].DisplayIndex = 0;
                InvokeColumnReordered(service, grid);
                DrainDispatcher();

                Assert.AreEqual(grid.Columns.Count, order.Count);
                foreach (var column in grid.Columns)
                {
                    var key = ColumnWidthNormalization.GetColumnKey(column);
                    Assert.AreEqual(column.DisplayIndex, order[key]);
                }
                Assert.AreEqual(1, saveCount);

                service.Detach();
            });
        }

        [TestMethod]
        public void LiveResize_ExpandingColumnShrinksPreferredNeighbor()
        {
            RunOnStaThread(() =>
            {
                var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var grid = CreateGrid();
                var service = CreateService(grid, order, () => { });
                InvokePrivate(service, "CaptureResizeObservedWidths");
                SetPrivateField(service, "_isResizeInProgress", true);
                SetPrivateField(service, "_lastResizeAbsorberColumnKey", "B");

                grid.Columns[0].Width = new DataGridLength(130, DataGridLengthUnitType.Pixel);
                InvokePrivate(service, "OnColumnWidthChanged", grid.Columns[0]);

                Assert.AreEqual(70d, grid.Columns[1].Width.DisplayValue);
            });
        }

        [TestMethod]
        public void Normalization_CrampedViewportLowersResizableColumnMinimums()
        {
            RunOnStaThread(() =>
            {
                var grid = CreateGrid();
                foreach (var column in grid.Columns)
                {
                    column.MinWidth = 100d;
                }

                var result = ColumnWidthNormalization.TryBuildNormalizedWidths(
                    grid,
                    protectedKey: null,
                    rescaleAll: true,
                    preferredWidthsByKey: new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),
                    fallbackAvailableWidth: 150d,
                    useEqualWidthForMissing: true,
                    out var normalized);

                Assert.IsTrue(result);
                Assert.AreEqual(150d, normalized["A"] + normalized["B"] + normalized["C"]);
                Assert.IsTrue(grid.Columns[0].MinWidth < 100d);
                Assert.IsTrue(grid.Columns[1].MinWidth < 100d);
                Assert.IsTrue(grid.Columns[2].MinWidth < 100d);
            });
        }

        [TestMethod]
        public void PersistPendingResizeWidths_ClearsInteractiveResizeContext()
        {
            RunOnStaThread(() =>
            {
                var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var saveCount = 0;
                var grid = CreateGrid();
                var service = CreateService(grid, order, () => saveCount++);

                InvokePrivate(service, "CaptureResizeObservedWidths");
                SetPrivateField(service, "_isResizeInProgress", true);

                grid.Columns[0].Width = new DataGridLength(130, DataGridLengthUnitType.Pixel);
                InvokePrivate(service, "OnColumnWidthChanged", grid.Columns[0]);

                Assert.AreEqual("A", GetPrivateField<string>(service, "_lastResizedColumnKey"));
                Assert.AreEqual("B", GetPrivateField<string>(service, "_lastResizeAbsorberColumnKey"));

                SetPrivateField(service, "_isResizeInProgress", false);
                InvokePrivate(service, "PersistPendingResizeWidths");

                Assert.IsNull(GetPrivateField<string>(service, "_lastResizedColumnKey"));
                Assert.IsNull(GetPrivateField<string>(service, "_lastResizeAbsorberColumnKey"));
                Assert.IsNull(GetPrivateField<string>(service, "_resizeBoundaryLeftColumnKey"));
                Assert.IsNull(GetPrivateField<string>(service, "_resizeBoundaryRightColumnKey"));
                Assert.AreEqual(0, GetPrivateField<Dictionary<string, double>>(service, "_resizeObservedWidths").Count);
                Assert.AreEqual(1, saveCount);
            });
        }

        [TestMethod]
        public void PinnedColumns_StayLeadingAgainstHostilePersistedOrder()
        {
            RunOnStaThread(() =>
            {
                // A map that puts a customizable column first and names two columns that no longer
                // exist: what a layout saved by an older build looks like.
                var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    ["C"] = 0,
                    ["Gone"] = 1,
                    ["AlsoGone"] = 2
                };

                var grid = CreateGridWithPinnedColumns();
                var service = CreateService(grid, order, () => { });
                service.PinnedLeadingKeys = new List<string> { "P0", "P1" };

                service.Attach();

                Assert.AreEqual(0, ColumnByKey(grid, "P0").DisplayIndex);
                Assert.AreEqual(1, ColumnByKey(grid, "P1").DisplayIndex);
                Assert.IsTrue(
                    ColumnByKey(grid, "C").DisplayIndex >= 2,
                    "A saved index of 0 must not pull a customizable column ahead of the pinned block.");

                service.Detach();
            });
        }

        [TestMethod]
        public void PinnedColumns_ClampAfterReorderAndPersistTheClampedLayout()
        {
            RunOnStaThread(() =>
            {
                var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var grid = CreateGridWithPinnedColumns();
                var service = CreateService(grid, order, () => { });
                service.PinnedLeadingKeys = new List<string> { "P0", "P1" };
                service.Attach();

                // What a drop to the left of the pinned block leaves behind.
                ColumnByKey(grid, "C").DisplayIndex = 0;

                InvokeColumnReordered(service, grid);
                DrainDispatcher();

                Assert.AreEqual(0, ColumnByKey(grid, "P0").DisplayIndex);
                Assert.AreEqual(1, ColumnByKey(grid, "P1").DisplayIndex);

                // The stored map must describe what the grid is showing, never the dropped state.
                if (order.TryGetValue("P0", out var savedPinned))
                {
                    Assert.AreEqual(0, savedPinned);
                }

                if (order.TryGetValue("C", out var savedMoved))
                {
                    Assert.AreEqual(
                        ColumnByKey(grid, "C").DisplayIndex,
                        savedMoved,
                        "The persisted index must match the clamped display index.");
                }

                service.Detach();
            });
        }

        [TestMethod]
        public void PinnedColumns_AreExcludedFromTheVisibilityMenu()
        {
            RunOnStaThread(() =>
            {
                var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var grid = CreateGridWithPinnedColumns();
                var service = CreateService(grid, order, () => { });
                service.PinnedLeadingKeys = new List<string> { "P0", "P1" };
                service.ExcludedVisibilityKeys.Add("P0");
                service.ExcludedVisibilityKeys.Add("P1");
                service.Attach();

                var menu = service.BuildColumnVisibilityMenu(ColumnByKey(grid, "C"));

                Assert.IsNotNull(menu);
                var headers = new List<string>();
                foreach (var item in menu.Items)
                {
                    if (item is MenuItem menuItem && menuItem.IsCheckable)
                    {
                        headers.Add(menuItem.Header as string);
                    }
                }

                CollectionAssert.DoesNotContain(headers, "P0");
                CollectionAssert.DoesNotContain(headers, "P1");
                CollectionAssert.Contains(headers, "C");

                service.Detach();
            });
        }

        [TestMethod]
        public void EmptyOrderMap_ClampsPinnedColumnsWithoutPersisting()
        {
            RunOnStaThread(() =>
            {
                var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var saveCount = 0;
                var grid = CreateGridWithPinnedColumns();

                // Declared out of order, so the clamp has something to do on a fresh install.
                ColumnByKey(grid, "P1").DisplayIndex = 3;

                var service = CreateService(grid, order, () => saveCount++);
                service.PinnedLeadingKeys = new List<string> { "P0", "P1" };

                service.Attach();
                DrainDispatcher();

                Assert.AreEqual(0, ColumnByKey(grid, "P0").DisplayIndex);
                Assert.AreEqual(1, ColumnByKey(grid, "P1").DisplayIndex);
                Assert.AreEqual(0, order.Count, "Applying a layout is not a user edit and must not persist one.");
                Assert.AreEqual(0, saveCount);

                service.Detach();
            });
        }

        [TestMethod]
        public void ResolveColumnDisplayName_PrefersTheDeclaredNameOverAControlHeader()
        {
            RunOnStaThread(() =>
            {
                var column = new DataGridTextColumn { Header = new Grid() };
                ColumnVisibilityHelper.SetColumnKey(column, "EditorName");
                ColumnVisibilityHelper.SetColumnDisplayName(column, "Achievement Name");

                var resolved = InvokeStaticPrivate<string>(
                    typeof(DataGridColumnLayoutService),
                    "ResolveColumnDisplayName",
                    column);

                Assert.AreEqual("Achievement Name", resolved);
            });
        }

        [TestMethod]
        public void ResolveColumnDisplayName_FallsBackToTheHeaderWhenNoNameIsDeclared()
        {
            RunOnStaThread(() =>
            {
                var column = new DataGridTextColumn { Header = "Points" };
                ColumnVisibilityHelper.SetColumnKey(column, "EditorPoints");

                var resolved = InvokeStaticPrivate<string>(
                    typeof(DataGridColumnLayoutService),
                    "ResolveColumnDisplayName",
                    column);

                Assert.AreEqual("Points", resolved);
            });
        }

        [TestMethod]
        public void SetColumnLocked_FreezesTheColumnAndPersistsItsWidthAndLock()
        {
            RunOnStaThread(() =>
            {
                var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var saveCount = 0;
                var maps = new PersistedMaps();
                var widths = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
                {
                    ["A"] = 100,
                    ["B"] = 120,
                    ["C"] = 100
                };
                var grid = CreateGrid();
                grid.Columns[1].MaxWidth = 400;
                var service = CreateService(grid, order, () => saveCount++, widths, maps: maps);
                service.Attach();
                DrainDispatcher();

                var column = ColumnByKey(grid, "B");
                Assert.IsTrue(service.CanLockColumn(column));

                service.SetColumnLocked(column, true);

                Assert.IsTrue(service.IsColumnLocked(column));
                // The lock never touches CanUserResize: the column keeps rescaling with the grid.
                Assert.IsTrue(column.CanUserResize);
                Assert.IsTrue(DataGridColumnGripperBehavior.GetIsLocked(column));
                Assert.IsTrue(column.Width.IsAbsolute);
                Assert.IsTrue(maps.Locks.TryGetValue("B", out var locked) && locked);
                Assert.AreEqual(column.Width.Value, maps.Widths["B"]);
                Assert.AreEqual(1, saveCount);
                Assert.IsFalse(service.CanLockColumn(column));

                service.SetColumnLocked(column, false);

                Assert.IsFalse(service.IsColumnLocked(column));
                Assert.IsTrue(column.CanUserResize);
                Assert.IsFalse(DataGridColumnGripperBehavior.GetIsLocked(column));
                Assert.IsFalse(maps.Locks.ContainsKey("B"));
                Assert.AreEqual(400d, column.MaxWidth);
                Assert.AreEqual(2, saveCount);

                service.Detach();
            });
        }

        [TestMethod]
        public void Attach_WithPersistedLockRestoresTheLockAndTheSavedWidth()
        {
            RunOnStaThread(() =>
            {
                var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var widths = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
                {
                    ["A"] = 100,
                    ["B"] = 150,
                    ["C"] = 100
                };
                var locks = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { ["B"] = true };
                var grid = CreateGrid();
                var service = CreateService(grid, order, () => { }, widths, initialLocks: locks);

                service.Attach();

                var column = ColumnByKey(grid, "B");
                Assert.IsTrue(service.IsColumnLocked(column));
                Assert.IsTrue(DataGridColumnGripperBehavior.GetIsLocked(column));
                Assert.IsTrue(column.CanUserResize);
                Assert.IsTrue(column.Width.IsAbsolute);
                Assert.AreEqual(150d, column.Width.Value);
                Assert.IsFalse(service.IsColumnLocked(ColumnByKey(grid, "A")));

                service.Detach();

                Assert.IsFalse(service.IsColumnLocked(column));
                Assert.IsFalse(DataGridColumnGripperBehavior.GetIsLocked(column));
            });
        }

        [TestMethod]
        public void CanLockColumn_IsFalseForTheLastUnlockedColumn()
        {
            RunOnStaThread(() =>
            {
                var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var grid = CreateGrid();
                var service = CreateService(grid, order, () => { });
                service.Attach();

                service.SetColumnLocked(ColumnByKey(grid, "A"), true);
                service.SetColumnLocked(ColumnByKey(grid, "B"), true);

                Assert.IsFalse(service.CanLockColumn(ColumnByKey(grid, "C")));
                service.SetColumnLocked(ColumnByKey(grid, "C"), true);
                Assert.IsFalse(service.IsColumnLocked(ColumnByKey(grid, "C")));

                service.Detach();
            });
        }

        [TestMethod]
        public void LockedColumn_StillRescalesWhenTheGridWidthChanges()
        {
            RunOnStaThread(() =>
            {
                var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var grid = CreateGrid();
                grid.Width = 360;
                grid.Height = 160;

                var window = new Window
                {
                    Width = 480,
                    Height = 220,
                    Content = grid,
                    ShowActivated = false
                };

                DataGridColumnLayoutService service = null;
                try
                {
                    window.Show();
                    grid.UpdateLayout();

                    service = CreateService(grid, order, () => { });
                    service.Attach();
                    DrainDispatcher();
                    grid.UpdateLayout();

                    var locked = ColumnByKey(grid, "B");
                    service.SetColumnLocked(locked, true);
                    DrainDispatcher();
                    grid.UpdateLayout();
                    var lockedBefore = locked.ActualWidth;
                    Assert.IsTrue(lockedBefore > 0);

                    grid.Width = 270;
                    grid.UpdateLayout();
                    DrainDispatcher();
                    grid.UpdateLayout();

                    var total = 0d;
                    foreach (var column in grid.Columns)
                    {
                        total += column.ActualWidth;
                    }

                    // Three-quarters of the width: the locked column shrinks with the others
                    // rather than holding its pixels and squeezing them.
                    Assert.IsTrue(locked.ActualWidth < lockedBefore - 10, $"locked {locked.ActualWidth} vs {lockedBefore}");
                    Assert.AreEqual(lockedBefore * 0.75, locked.ActualWidth, 6d);
                    Assert.IsTrue(total <= 270.5, $"total {total}");
                }
                finally
                {
                    service?.Detach();
                    window.Close();
                }
            });
        }

        [TestMethod]
        public void SetColumnWidthFromInput_NeverTakesSpaceFromALockedColumn()
        {
            RunOnStaThread(() =>
            {
                var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var grid = CreateGrid();
                grid.Width = 360;
                grid.Height = 160;

                var window = new Window
                {
                    Width = 420,
                    Height = 220,
                    Content = grid,
                    ShowActivated = false
                };

                DataGridColumnLayoutService service = null;
                try
                {
                    window.Show();
                    grid.UpdateLayout();

                    service = CreateService(grid, order, () => { });
                    service.Attach();
                    DrainDispatcher();
                    grid.UpdateLayout();

                    // A | B(locked) | C: widening A must skip B and take from C.
                    var locked = ColumnByKey(grid, "B");
                    service.SetColumnLocked(locked, true);
                    DrainDispatcher();
                    grid.UpdateLayout();
                    var lockedBefore = locked.ActualWidth;
                    var absorber = ColumnByKey(grid, "C");
                    var absorberBefore = absorber.ActualWidth;

                    var target = ColumnByKey(grid, "A");
                    var typed = Math.Round(target.ActualWidth) + 30;
                    service.SetColumnWidthFromInput(target, typed);
                    DrainDispatcher();
                    grid.UpdateLayout();

                    Assert.AreEqual(typed, target.ActualWidth, 1d);
                    Assert.AreEqual(lockedBefore, locked.ActualWidth, 1d);
                    Assert.AreEqual(absorberBefore - 30, absorber.ActualWidth, 1d);
                }
                finally
                {
                    service?.Detach();
                    window.Close();
                }
            });
        }

        [TestMethod]
        public void BuildColumnVisibilityMenu_WithoutAlignmentDelegates_NamesTheColumnAboveAWidthRow()
        {
            RunOnStaThread(() =>
            {
                var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var grid = CreateGrid();
                var service = CreateService(grid, order, () => { });
                service.Attach();

                var menu = service.BuildColumnVisibilityMenu(ColumnByKey(grid, "B"));

                Assert.IsNotNull(menu);
                var header = menu.Items[0] as MenuItem;
                Assert.IsNotNull(header);
                Assert.AreEqual("B", (header.Header as TextBlock)?.Text);

                var widthRow = (menu.Items[1] as MenuItem)?.Header as StackPanel;
                Assert.IsNotNull(widthRow);
                Assert.AreEqual(2, widthRow.Children.Count);
                var editor = widthRow.Children[0] as TextBox;
                Assert.IsNotNull(editor);
                Assert.IsTrue(editor.Text.EndsWith(" px", StringComparison.Ordinal));
                Assert.IsInstanceOfType(widthRow.Children[1], typeof(Button));
                Assert.IsTrue(((Button)widthRow.Children[1]).IsEnabled);

                Assert.IsInstanceOfType(menu.Items[2], typeof(Separator));

                service.Detach();
            });
        }

        [TestMethod]
        public void SetColumnWidthFromInput_OnAnUnlockedColumnMovesTheBoundaryAndKeepsTheTotal()
        {
            RunOnStaThread(() =>
            {
                var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var grid = CreateGrid();
                grid.Width = 360;
                grid.Height = 160;

                var window = new Window
                {
                    Width = 420,
                    Height = 220,
                    Content = grid,
                    ShowActivated = false
                };

                DataGridColumnLayoutService service = null;
                try
                {
                    window.Show();
                    grid.UpdateLayout();

                    service = CreateService(grid, order, () => { });
                    service.Attach();
                    DrainDispatcher();
                    grid.UpdateLayout();

                    var before = 0d;
                    foreach (var column in grid.Columns)
                    {
                        before += column.ActualWidth;
                    }

                    var target = ColumnByKey(grid, "B");
                    var typed = Math.Round(target.ActualWidth) + 30;
                    service.SetColumnWidthFromInput(target, typed);
                    DrainDispatcher();
                    grid.UpdateLayout();

                    var after = 0d;
                    foreach (var column in grid.Columns)
                    {
                        after += column.ActualWidth;
                    }

                    Assert.AreEqual(typed, target.ActualWidth, 1d);
                    Assert.AreEqual(before, after, 1d);
                }
                finally
                {
                    service?.Detach();
                    window.Close();
                }
            });
        }

        [TestMethod]
        public void LiveResize_ShowsWidthPillsOverBothBoundaryColumnsUntilTheDragEnds()
        {
            RunOnStaThread(() =>
            {
                var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var grid = CreateGrid();
                grid.Width = 360;
                grid.Height = 160;

                var window = new Window
                {
                    Width = 420,
                    Height = 220,
                    Content = grid,
                    ShowActivated = false
                };

                DataGridColumnLayoutService service = null;
                try
                {
                    window.Show();
                    grid.UpdateLayout();

                    service = CreateService(grid, order, () => { });
                    service.Attach();
                    DrainDispatcher();
                    grid.UpdateLayout();

                    var layer = AdornerLayer.GetAdornerLayer(grid);
                    Assert.IsNotNull(layer);
                    Assert.IsNull(layer.GetAdorners(grid));

                    InvokePrivate(service, "CaptureResizeObservedWidths");
                    SetPrivateField(service, "_isResizeInProgress", true);
                    SetPrivateField(service, "_resizeBoundaryLeftColumnKey", "A");
                    SetPrivateField(service, "_resizeBoundaryRightColumnKey", "B");

                    var resized = ColumnByKey(grid, "A");
                    resized.Width = new DataGridLength(Math.Round(resized.ActualWidth) + 20, DataGridLengthUnitType.Pixel);
                    InvokePrivate(service, "OnColumnWidthChanged", resized);
                    grid.UpdateLayout();

                    var adorners = layer.GetAdorners(grid);
                    Assert.IsNotNull(adorners);
                    Assert.AreEqual(1, adorners.Length);
                    var pills = FindVisualChildren<TextBlock>(adorners[0]);
                    Assert.AreEqual(2, pills.Count);
                    Assert.AreEqual(resized.Width.Value.ToString("0") + " px", pills[0].Text);
                    Assert.IsTrue(pills[1].Text.EndsWith(" px", StringComparison.Ordinal));

                    InvokePrivate(service, "CompleteResizeNormalization");
                    DrainDispatcher();

                    Assert.IsNull(layer.GetAdorners(grid));
                }
                finally
                {
                    service?.Detach();
                    window.Close();
                }
            });
        }

        private static List<T> FindVisualChildren<T>(DependencyObject root)
            where T : DependencyObject
        {
            var result = new List<T>();
            if (root == null)
            {
                return result;
            }

            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T typed)
                {
                    result.Add(typed);
                }

                result.AddRange(FindVisualChildren<T>(child));
            }

            return result;
        }

        /// <summary>
        /// Two pinned columns ahead of three customizable ones, shaped like the achievement editor:
        /// the pinned pair is fixed width and undraggable, the rest resize and reorder.
        /// </summary>
        private static DataGrid CreateGridWithPinnedColumns()
        {
            var grid = new DataGrid();
            grid.Columns.Add(CreatePinnedColumn("P0"));
            grid.Columns.Add(CreatePinnedColumn("P1"));
            grid.Columns.Add(CreateColumn("A"));
            grid.Columns.Add(CreateColumn("B"));
            grid.Columns.Add(CreateColumn("C"));
            return grid;
        }

        private static DataGridColumn CreatePinnedColumn(string key)
        {
            var column = new DataGridTextColumn
            {
                Header = key,
                CanUserResize = false,
                CanUserReorder = false,
                Width = new DataGridLength(40, DataGridLengthUnitType.Pixel)
            };
            ColumnVisibilityHelper.SetColumnKey(column, key);
            return column;
        }

        private static DataGridColumn ColumnByKey(DataGrid grid, string key)
        {
            foreach (var column in grid.Columns)
            {
                if (string.Equals(ColumnVisibilityHelper.GetColumnKey(column), key, StringComparison.OrdinalIgnoreCase))
                {
                    return column;
                }
            }

            Assert.Fail($"No column with key {key}.");
            return null;
        }

        private static T InvokeStaticPrivate<T>(Type type, string methodName, params object[] args)
        {
            var method = type.GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(method);
            return (T)method.Invoke(null, args);
        }

        /// <summary>
        /// The maps a service writes through its delegates, so a test can read back what it persisted.
        /// </summary>
        private sealed class PersistedMaps
        {
            public Dictionary<string, double> Widths = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, bool> Visibility = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, bool> Locks = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        }

        private static DataGridColumnLayoutService CreateService(
            DataGrid grid,
            Dictionary<string, int> order,
            Action saveSettings,
            Dictionary<string, double> initialWidths = null,
            Dictionary<string, bool> initialVisibility = null,
            IReadOnlyDictionary<string, double> defaultWidthSeeds = null,
            Dictionary<string, bool> initialLocks = null,
            PersistedMaps maps = null)
        {
            maps = maps ?? new PersistedMaps();
            if (initialWidths != null)
            {
                maps.Widths = new Dictionary<string, double>(initialWidths, StringComparer.OrdinalIgnoreCase);
            }

            if (initialVisibility != null)
            {
                maps.Visibility = new Dictionary<string, bool>(initialVisibility, StringComparer.OrdinalIgnoreCase);
            }

            if (initialLocks != null)
            {
                maps.Locks = new Dictionary<string, bool>(initialLocks, StringComparer.OrdinalIgnoreCase);
            }

            return new DataGridColumnLayoutService(
                grid,
                logger: null,
                getWidths: () => maps.Widths,
                setWidths: map => maps.Widths = map,
                getVisibility: () => maps.Visibility,
                setVisibility: map => maps.Visibility = map,
                saveSettings: saveSettings,
                defaultWidthSeeds: defaultWidthSeeds,
                getOrder: () => order,
                setOrder: map =>
                {
                    if (ReferenceEquals(order, map) || map == null)
                    {
                        return;
                    }

                    order.Clear();
                    foreach (var pair in map)
                    {
                        order[pair.Key] = pair.Value;
                    }
                },
                getLocks: () => maps.Locks,
                setLocks: map => maps.Locks = map);
        }

        private static DataGrid CreateGrid()
        {
            var grid = new DataGrid();
            grid.Columns.Add(CreateColumn("A"));
            grid.Columns.Add(CreateColumn("B"));
            grid.Columns.Add(CreateColumn("C"));
            return grid;
        }

        private static DataGridColumn CreateColumn(string key)
        {
            var column = new DataGridTextColumn
            {
                Header = key,
                CanUserResize = true,
                Width = new DataGridLength(100, DataGridLengthUnitType.Pixel)
            };
            ColumnVisibilityHelper.SetColumnKey(column, key);
            return column;
        }

        private static void InvokeColumnReordered(DataGridColumnLayoutService service, DataGrid grid)
        {
            InvokePrivate(service, "Grid_ColumnReordered", grid, null);
        }

        private static void InvokePrivate(object target, string methodName, params object[] args)
        {
            var method = target.GetType().GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(method);
            method.Invoke(target, args);
        }

        private static void SetPrivateField(object target, string fieldName, object value)
        {
            var field = target.GetType().GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field);
            field.SetValue(target, value);
        }

        private static T GetPrivateField<T>(object target, string fieldName)
        {
            var field = target.GetType().GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field);
            return (T)field.GetValue(target);
        }

        private static void DrainDispatcher()
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.Invoke(new Action(() => { }), DispatcherPriority.Background);
            dispatcher.Invoke(new Action(() => { }), DispatcherPriority.ApplicationIdle);
        }

        private static void RunOnStaThread(Action action)
        {
            Exception error = null;
            var thread = new Thread(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    error = ex is TargetInvocationException invocationException && invocationException.InnerException != null
                        ? invocationException.InnerException
                        : ex;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            if (error != null)
            {
                throw new AssertFailedException(error.ToString());
            }
        }
    }
}
