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
using System.Windows.Markup;
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
        public void RightmostColumnHover_ShowsEnabledScrollBarsWithoutChangingLayout()
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
                    var leftCell = GetCell(grid, displayIndex: 0);
                    var rightmostCell = GetCell(grid, displayIndex: 1);

                    RaiseMouseMove(leftCell);
                    DrainDispatcher();

                    Assert.AreEqual(0d, scrollBar.Opacity, 0.001d);
                    Assert.IsFalse(scrollBar.IsHitTestVisible);

                    RaiseMouseMove(rightmostCell);
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
        public void ScrollingGrid_ShowsScrollbarAwayFromRightmostColumn()
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
                    var leftCell = GetCell(grid, displayIndex: 0);

                    RaiseMouseMove(leftCell);
                    DrainDispatcher();

                    Assert.AreEqual(0d, scrollBar.Opacity, 0.001d);
                    Assert.IsFalse(scrollBar.IsHitTestVisible);

                    scrollViewer.ScrollToVerticalOffset(10);
                    DrainDispatcher();

                    Assert.IsTrue(scrollBar.Opacity > 0d);
                    Assert.IsTrue(scrollBar.IsHitTestVisible);
                }
                finally
                {
                    window?.Close();
                }
            });
        }

        [TestMethod]
        public void MouseWheel_FullyShowsScrollbarAwayFromRightmostColumn()
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
                    var leftCell = GetCell(grid, displayIndex: 0);

                    RaiseMouseMove(leftCell);
                    DrainDispatcher();

                    Assert.AreEqual(0d, scrollBar.Opacity, 0.001d);
                    Assert.IsFalse(scrollBar.IsHitTestVisible);

                    RaisePreviewMouseWheel(leftCell, delta: -120);
                    DrainDispatcher();

                    Assert.AreEqual(1d, scrollBar.Opacity, 0.001d);
                    Assert.IsTrue(scrollBar.IsHitTestVisible);
                }
                finally
                {
                    window?.Close();
                }
            });
        }

        [TestMethod]
        public void ActiveGrid_ShowsIdleScrollbarUntilScrollbarIsHovered()
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
                    var rightmostCell = GetCell(grid, displayIndex: 1);

                    RaiseMouseMove(rightmostCell);
                    DrainDispatcher();

                    var idleOpacity = scrollBar.Opacity;
                    Assert.IsTrue(idleOpacity > 0d);
                    Assert.IsTrue(idleOpacity < 0.8d);
                    Assert.IsTrue(scrollBar.IsHitTestVisible);

                    RaiseMouseEnter(scrollBar);
                    DrainDispatcher();

                    Assert.IsTrue(scrollBar.Opacity > idleOpacity);

                    RaiseMouseLeave(scrollBar);
                    DrainDispatcher();

                    Assert.AreEqual(idleOpacity, scrollBar.Opacity, 0.001d);
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
                    var rightmostCell = GetCell(grid, displayIndex: 1);
                    scrollBar.IsEnabled = false;
                    DrainDispatcher();

                    RaiseMouseMove(rightmostCell);
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

        [TestMethod]
        public void OverlayTemplate_VerticalScrollBarStartsBelowHeadersAndDoesNotReserveViewportWidth()
        {
            RunOnStaThread(() =>
            {
                var defaultGrid = CreateScrollableGrid();
                Window defaultWindow = null;
                var defaultViewportWidth = 0d;

                try
                {
                    defaultWindow = ShowGrid(defaultGrid);
                    defaultViewportWidth = GetScrollViewer(defaultGrid).ViewportWidth;
                }
                finally
                {
                    defaultWindow?.Close();
                }

                var overlayGrid = CreateScrollableGrid();
                overlayGrid.Template = CreateOverlayDataGridTemplate();
                Window overlayWindow = null;

                try
                {
                    DataGridHoverScrollBarBehavior.SetIsEnabled(overlayGrid, true);
                    overlayWindow = ShowGrid(overlayGrid);

                    var scrollViewer = GetScrollViewer(overlayGrid);
                    var scrollBar = GetVerticalScrollBar(overlayGrid);
                    var headersPresenter = VisualTreeHelpers.FindVisualChild<DataGridColumnHeadersPresenter>(overlayGrid);

                    Assert.IsNotNull(headersPresenter);
                    Assert.IsTrue(scrollBar.ActualWidth > 0d);
                    Assert.IsTrue(
                        scrollViewer.ViewportWidth >= defaultViewportWidth + scrollBar.ActualWidth - 1.5d,
                        $"Expected overlay viewport width {scrollViewer.ViewportWidth} to include the vertical scrollbar width {scrollBar.ActualWidth}; default viewport was {defaultViewportWidth}.");

                    var scrollbarTop = scrollBar.TranslatePoint(new Point(0, 0), overlayGrid).Y;
                    var headersBottom = headersPresenter.TranslatePoint(new Point(0, headersPresenter.ActualHeight), overlayGrid).Y;

                    Assert.IsTrue(
                        scrollbarTop >= headersBottom - 1d,
                        $"Expected overlay scrollbar top {scrollbarTop} to start below header bottom {headersBottom}.");
                }
                finally
                {
                    overlayWindow?.Close();
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

        private static ControlTemplate CreateOverlayDataGridTemplate()
        {
            const string xaml = @"
<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                 xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
                 TargetType='{x:Type DataGrid}'>
    <Border Background='{TemplateBinding Background}'
            BorderBrush='{TemplateBinding BorderBrush}'
            BorderThickness='{TemplateBinding BorderThickness}'
            Padding='{TemplateBinding Padding}'
            SnapsToDevicePixels='{TemplateBinding SnapsToDevicePixels}'>
        <ScrollViewer x:Name='DG_ScrollViewer'
                      Focusable='False'>
            <ScrollViewer.Template>
                <ControlTemplate TargetType='{x:Type ScrollViewer}'>
                    <Grid SnapsToDevicePixels='{TemplateBinding SnapsToDevicePixels}'>
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width='Auto'/>
                            <ColumnDefinition Width='*'/>
                        </Grid.ColumnDefinitions>
                        <Grid.RowDefinitions>
                            <RowDefinition Height='Auto'/>
                            <RowDefinition Height='*'/>
                            <RowDefinition Height='Auto'/>
                        </Grid.RowDefinitions>

                        <Button Command='{x:Static DataGrid.SelectAllCommand}'
                                Width='{Binding RelativeSource={RelativeSource AncestorType={x:Type DataGrid}}, Path=CellsPanelHorizontalOffset}'
                                Focusable='False'
                                Visibility='{Binding RelativeSource={RelativeSource AncestorType={x:Type DataGrid}}, Path=HeadersVisibility, Converter={x:Static DataGrid.HeadersVisibilityConverter}, ConverterParameter={x:Static DataGridHeadersVisibility.All}}'/>

                        <DataGridColumnHeadersPresenter x:Name='PART_ColumnHeadersPresenter'
                                                        Grid.Column='1'
                                                        Visibility='{Binding RelativeSource={RelativeSource AncestorType={x:Type DataGrid}}, Path=HeadersVisibility, Converter={x:Static DataGrid.HeadersVisibilityConverter}, ConverterParameter={x:Static DataGridHeadersVisibility.Column}}'/>

                        <ScrollContentPresenter x:Name='PART_ScrollContentPresenter'
                                                Grid.Row='1'
                                                Grid.ColumnSpan='2'
                                                CanContentScroll='{TemplateBinding CanContentScroll}'
                                                Content='{TemplateBinding Content}'
                                                ContentStringFormat='{TemplateBinding ContentStringFormat}'
                                                ContentTemplate='{TemplateBinding ContentTemplate}'
                                                Margin='{TemplateBinding Padding}'/>

                        <ScrollBar x:Name='PART_VerticalScrollBar'
                                   Grid.Row='1'
                                   Grid.Column='1'
                                   HorizontalAlignment='Right'
                                   VerticalAlignment='Stretch'
                                   Panel.ZIndex='10'
                                   Orientation='Vertical'
                                   Maximum='{TemplateBinding ScrollableHeight}'
                                   ViewportSize='{TemplateBinding ViewportHeight}'
                                   Value='{Binding RelativeSource={RelativeSource TemplatedParent}, Path=VerticalOffset, Mode=OneWay}'
                                   Visibility='{TemplateBinding ComputedVerticalScrollBarVisibility}'/>

                        <Grid Grid.Row='2'
                              Grid.Column='1'>
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width='{Binding RelativeSource={RelativeSource AncestorType={x:Type DataGrid}}, Path=NonFrozenColumnsViewportHorizontalOffset}'/>
                                <ColumnDefinition Width='*'/>
                            </Grid.ColumnDefinitions>
                            <ScrollBar x:Name='PART_HorizontalScrollBar'
                                       Grid.Column='1'
                                       Orientation='Horizontal'
                                       Maximum='{TemplateBinding ScrollableWidth}'
                                       ViewportSize='{TemplateBinding ViewportWidth}'
                                       Value='{Binding RelativeSource={RelativeSource TemplatedParent}, Path=HorizontalOffset, Mode=OneWay}'
                                       Visibility='{TemplateBinding ComputedHorizontalScrollBarVisibility}'/>
                        </Grid>
                    </Grid>
                </ControlTemplate>
            </ScrollViewer.Template>
            <ItemsPresenter SnapsToDevicePixels='{TemplateBinding SnapsToDevicePixels}'/>
        </ScrollViewer>
    </Border>
</ControlTemplate>";

            return (ControlTemplate)XamlReader.Parse(xaml);
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

        private static DataGridCell GetCell(DataGrid grid, int displayIndex)
        {
            grid.UpdateLayout();
            DrainDispatcher();

            var cell = EnumerateVisualDescendants<DataGridCell>(grid)
                .FirstOrDefault(candidate =>
                    candidate.Column != null &&
                    candidate.Column.DisplayIndex == displayIndex);
            Assert.IsNotNull(cell);
            return cell;
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

        private static void RaiseMouseEnter(UIElement element)
        {
            Assert.IsNotNull(Mouse.PrimaryDevice);
            element.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
            {
                RoutedEvent = Mouse.MouseEnterEvent
            });
        }

        private static void RaiseMouseLeave(UIElement element)
        {
            Assert.IsNotNull(Mouse.PrimaryDevice);
            element.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
            {
                RoutedEvent = Mouse.MouseLeaveEvent
            });
        }

        private static void RaiseMouseMove(UIElement element)
        {
            Assert.IsNotNull(Mouse.PrimaryDevice);

            // Real WPF input raises the tunneling preview first, then the bubbling event.
            // The behavior listens on PreviewMouseMove so it observes the cursor even when a
            // descendant (e.g. a column header) marks the bubbling MouseMove handled, so the
            // test must raise the preview event to exercise the production path.
            element.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
            {
                RoutedEvent = Mouse.PreviewMouseMoveEvent
            });
            element.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
            {
                RoutedEvent = Mouse.MouseMoveEvent
            });
        }

        private static void RaisePreviewMouseWheel(UIElement element, int delta)
        {
            Assert.IsNotNull(Mouse.PrimaryDevice);
            element.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
            {
                RoutedEvent = Mouse.PreviewMouseWheelEvent
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
