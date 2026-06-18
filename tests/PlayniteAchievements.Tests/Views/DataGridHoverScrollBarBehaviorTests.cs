using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Tests.Views
{
    [TestClass]
    public class DataGridHoverScrollBarBehaviorTests
    {
        [TestMethod]
        public void Enabled_HidesTemplateScrollBarsAfterLoad()
        {
            RunOnStaThread(() =>
            {
                var grid = CreateScrollableGrid();
                Window window = null;

                try
                {
                    DataGridHoverScrollBarBehavior.SetIsEnabled(grid, true);
                    window = ShowGrid(grid);

                    var scrollBar = GetVerticalScrollBar(grid);

                    Assert.AreEqual(Visibility.Visible, scrollBar.Visibility);
                    Assert.AreEqual(0d, scrollBar.Opacity, 0.001d);
                    Assert.IsFalse(scrollBar.IsHitTestVisible);
                }
                finally
                {
                    window?.Close();
                }
            });
        }

        [TestMethod]
        public void MouseOver_ShowsEnabledScrollBarsWithoutChangingLayout()
        {
            RunOnStaThread(() =>
            {
                var grid = CreateScrollableGrid();
                Window window = null;

                try
                {
                    DataGridHoverScrollBarBehavior.SetIsEnabled(grid, true);
                    window = ShowGrid(grid);

                    var scrollViewer = GetScrollViewer(grid);
                    var scrollBar = GetVerticalScrollBar(grid);
                    var originalVisibility = scrollBar.Visibility;
                    var originalViewportWidth = scrollViewer.ViewportWidth;

                    RaiseMouseEnter(grid);
                    DrainDispatcher();

                    Assert.AreEqual(originalVisibility, scrollBar.Visibility);
                    Assert.AreEqual(originalViewportWidth, scrollViewer.ViewportWidth, 0.1d);
                    Assert.IsTrue(scrollBar.Opacity > 0d);
                    Assert.IsTrue(scrollBar.IsHitTestVisible);

                    RaiseMouseLeave(grid);
                    DrainDispatcher();

                    Assert.AreEqual(originalVisibility, scrollBar.Visibility);
                    Assert.AreEqual(originalViewportWidth, scrollViewer.ViewportWidth, 0.1d);
                    Assert.AreEqual(0d, scrollBar.Opacity, 0.001d);
                    Assert.IsFalse(scrollBar.IsHitTestVisible);
                }
                finally
                {
                    window?.Close();
                }
            });
        }

        [TestMethod]
        public void DisabledScrollBarsStayHiddenWhileMouseOver()
        {
            RunOnStaThread(() =>
            {
                var grid = CreateScrollableGrid();
                Window window = null;

                try
                {
                    DataGridHoverScrollBarBehavior.SetIsEnabled(grid, true);
                    window = ShowGrid(grid);

                    var scrollBar = GetVerticalScrollBar(grid);
                    scrollBar.IsEnabled = false;
                    DrainDispatcher();

                    RaiseMouseEnter(grid);
                    DrainDispatcher();

                    Assert.AreEqual(0d, scrollBar.Opacity, 0.001d);
                    Assert.IsFalse(scrollBar.IsHitTestVisible);
                }
                finally
                {
                    window?.Close();
                }
            });
        }

        [TestMethod]
        public void DisabledBehavior_RestoresOriginalScrollbarLocalValues()
        {
            RunOnStaThread(() =>
            {
                var grid = CreateScrollableGrid();
                Window window = null;

                try
                {
                    window = ShowGrid(grid);

                    var scrollBar = GetVerticalScrollBar(grid);
                    scrollBar.Opacity = 0.42d;
                    scrollBar.IsHitTestVisible = false;

                    DataGridHoverScrollBarBehavior.SetIsEnabled(grid, true);
                    DrainDispatcher();

                    Assert.AreEqual(0d, scrollBar.Opacity, 0.001d);
                    Assert.IsFalse(scrollBar.IsHitTestVisible);

                    DataGridHoverScrollBarBehavior.SetIsEnabled(grid, false);
                    DrainDispatcher();

                    Assert.AreEqual(0.42d, scrollBar.Opacity, 0.001d);
                    Assert.IsFalse(scrollBar.IsHitTestVisible);
                }
                finally
                {
                    window?.Close();
                }
            });
        }

        private static DataGrid CreateScrollableGrid()
        {
            var grid = new DataGrid
            {
                Width = 260,
                Height = 150,
                AutoGenerateColumns = false,
                IsReadOnly = true,
                CanUserAddRows = false,
                ItemsSource = Enumerable.Range(0, 80)
                    .Select(index => new TestRow
                    {
                        Name = "Achievement " + index,
                        Value = index
                    })
                    .ToList()
            };

            grid.Columns.Add(new DataGridTextColumn
            {
                Header = "Name",
                Binding = new Binding(nameof(TestRow.Name)),
                Width = new DataGridLength(180, DataGridLengthUnitType.Pixel)
            });
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = "Value",
                Binding = new Binding(nameof(TestRow.Value)),
                Width = new DataGridLength(80, DataGridLengthUnitType.Pixel)
            });

            ScrollViewer.SetVerticalScrollBarVisibility(grid, ScrollBarVisibility.Visible);
            ScrollViewer.SetHorizontalScrollBarVisibility(grid, ScrollBarVisibility.Disabled);
            return grid;
        }

        private static Window ShowGrid(DataGrid grid)
        {
            var window = new Window
            {
                Width = 340,
                Height = 240,
                Content = grid,
                ShowActivated = false
            };

            window.Show();
            grid.UpdateLayout();
            DrainDispatcher();
            return window;
        }

        private static ScrollViewer GetScrollViewer(DataGrid grid)
        {
            var scrollViewer = VisualTreeHelpers.FindVisualChild<ScrollViewer>(grid);
            Assert.IsNotNull(scrollViewer);
            return scrollViewer;
        }

        private static ScrollBar GetVerticalScrollBar(DataGrid grid)
        {
            var scrollBar = GetTemplateScrollBars(grid)
                .FirstOrDefault(bar => bar.Orientation == Orientation.Vertical);
            Assert.IsNotNull(scrollBar);
            return scrollBar;
        }

        private static List<ScrollBar> GetTemplateScrollBars(DataGrid grid)
        {
            var scrollViewer = GetScrollViewer(grid);
            scrollViewer.ApplyTemplate();

            var result = new List<ScrollBar>();
            AddIfMissing(result, scrollViewer.Template?.FindName("PART_VerticalScrollBar", scrollViewer) as ScrollBar);
            AddIfMissing(result, scrollViewer.Template?.FindName("PART_HorizontalScrollBar", scrollViewer) as ScrollBar);

            foreach (var scrollBar in EnumerateVisualDescendants<ScrollBar>(scrollViewer))
            {
                if (ReferenceEquals(scrollBar.TemplatedParent, scrollViewer) ||
                    string.Equals(scrollBar.Name, "PART_VerticalScrollBar", StringComparison.Ordinal) ||
                    string.Equals(scrollBar.Name, "PART_HorizontalScrollBar", StringComparison.Ordinal))
                {
                    AddIfMissing(result, scrollBar);
                }
            }

            return result;
        }

        private static void AddIfMissing(ICollection<ScrollBar> scrollBars, ScrollBar scrollBar)
        {
            if (scrollBar != null && !scrollBars.Contains(scrollBar))
            {
                scrollBars.Add(scrollBar);
            }
        }

        private static IEnumerable<T> EnumerateVisualDescendants<T>(DependencyObject parent)
            where T : DependencyObject
        {
            if (parent == null)
            {
                yield break;
            }

            var childCount = 0;
            try
            {
                childCount = VisualTreeHelper.GetChildrenCount(parent);
            }
            catch (ArgumentException)
            {
                yield break;
            }
            catch (InvalidOperationException)
            {
                yield break;
            }

            for (var i = 0; i < childCount; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typed)
                {
                    yield return typed;
                }

                foreach (var nested in EnumerateVisualDescendants<T>(child))
                {
                    yield return nested;
                }
            }
        }

        private static void RaiseMouseEnter(DataGrid grid)
        {
            Assert.IsNotNull(Mouse.PrimaryDevice);
            grid.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
            {
                RoutedEvent = Mouse.MouseEnterEvent
            });
        }

        private static void RaiseMouseLeave(DataGrid grid)
        {
            Assert.IsNotNull(Mouse.PrimaryDevice);
            grid.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
            {
                RoutedEvent = Mouse.MouseLeaveEvent
            });
        }

        private static void DrainDispatcher()
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.Invoke(new Action(() => { }), DispatcherPriority.Loaded);
            dispatcher.Invoke(new Action(() => { }), DispatcherPriority.Input);
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

        private sealed class TestRow
        {
            public string Name { get; set; }

            public int Value { get; set; }
        }
    }
}
