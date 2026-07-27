using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
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

        private static DataGridColumnLayoutService CreateService(
            DataGrid grid,
            Dictionary<string, int> order,
            Action saveSettings,
            Dictionary<string, double> initialWidths = null,
            Dictionary<string, bool> initialVisibility = null,
            IReadOnlyDictionary<string, double> defaultWidthSeeds = null)
        {
            var widths = initialWidths != null
                ? new Dictionary<string, double>(initialWidths, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var visibility = initialVisibility != null
                ? new Dictionary<string, bool>(initialVisibility, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            return new DataGridColumnLayoutService(
                grid,
                logger: null,
                getWidths: () => widths,
                setWidths: map => widths = map,
                getVisibility: () => visibility,
                setVisibility: map => visibility = map,
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
                });
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
