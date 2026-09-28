using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Views.Controls;

namespace PlayniteAchievements.Tests.Views
{
    [TestClass]
    public class DashboardOverlayLayoutTests
    {
        [TestMethod]
        public void OverlayHost_ReportsNoSizeMirrorsGridPositionAndArrangesTheChildToTheCell()
        {
            RunOnStaThread(() =>
            {
                var chevron = new Border { Width = 28, Height = 28, VerticalAlignment = VerticalAlignment.Bottom };
                Grid.SetRow(chevron, 2);
                Grid.SetColumn(chevron, 1);
                Grid.SetRowSpan(chevron, 1);
                Grid.SetColumnSpan(chevron, 3);
                Panel.SetZIndex(chevron, 39);

                var host = OverlayLayoutHost.Wrap(chevron);

                Assert.AreEqual(2, Grid.GetRow(host));
                Assert.AreEqual(1, Grid.GetColumn(host));
                Assert.AreEqual(3, Grid.GetColumnSpan(host));
                Assert.AreEqual(39, Panel.GetZIndex(host));
                Assert.AreSame(host, OverlayLayoutHost.HostOf(chevron));
                Assert.AreSame(host, OverlayLayoutHost.HostOf(host));

                Grid.SetColumn(chevron, 4);
                Assert.AreEqual(4, Grid.GetColumn(host), "later moves of the element follow through");

                host.Measure(new Size(300, 20));
                Assert.AreEqual(0, host.DesiredSize.Width);
                Assert.AreEqual(0, host.DesiredSize.Height);

                host.Arrange(new Rect(0, 0, 300, 20));
                Assert.AreEqual(300, host.RenderSize.Width);
                Assert.AreEqual(28, chevron.RenderSize.Height, "the child keeps its own size inside the cell");
            });
        }

        [TestMethod]
        public void FrameworkElementChildren_CannotInflateAStarTrackOnTheirOwn()
        {
            // FrameworkElement.MeasureCore clamps DesiredSize to the offered size, so a single-cell
            // child never raises a star track's minimum; the clamp below guards the paths that
            // bypass that (raw UIElements, span distribution under layout rounding).
            RunOnStaThread(() =>
            {
                var plain = TwoStarRows();
                var control = new Border { Height = 80 };
                Grid.SetRow(control, 0);
                plain.Children.Add(control);

                plain.Measure(new Size(200, 100));

                Assert.AreEqual(50, control.DesiredSize.Height);
                Assert.AreEqual(50, plain.DesiredSize.Height);
            });
        }

        [TestMethod]
        public void ClampedGrid_NeverAsksForMoreThanItsSlotWhenAChildInflatesATrack()
        {
            RunOnStaThread(() =>
            {
                // An all-star Grid already caps each track's minimum at its share, so the plain
                // grid reports the slot here too; the clamp is the guarantee for mixed tracks
                // and rounding arithmetic, which this test pins at the boundary.
                var plain = FillRows(TwoStarRows());
                plain.Measure(new Size(200, 100));
                Assert.IsTrue(plain.DesiredSize.Height <= 100);

                var clamped = FillRows(TwoStarRows(new ClampedDesiredSizeGrid()));
                clamped.Measure(new Size(200, 100));
                Assert.AreEqual(100, clamped.DesiredSize.Height);
            });
        }

        [TestMethod]
        public void OverlayHost_KeepsAnInflatingHandleOutOfTheGridMeasure()
        {
            RunOnStaThread(() =>
            {
                var grid = TwoStarRows();
                var handle = new UnclampedElement(80);
                Grid.SetRow(handle, 0);
                var host = new OverlayLayoutHost { Child = handle };
                Grid.SetRow(host, 0);
                grid.Children.Add(host);

                grid.Measure(new Size(200, 100));

                Assert.AreEqual(0, grid.DesiredSize.Height);
            });
        }

        // An 80px-tall inflating child in each of the two 50px star rows.
        private static Grid FillRows(Grid grid)
        {
            for (var row = 0; row < 2; row++)
            {
                var child = new UnclampedElement(80);
                Grid.SetRow(child, row);
                grid.Children.Add(child);
            }

            return grid;
        }

        private static Grid TwoStarRows(Grid grid = null)
        {
            grid = grid ?? new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            return grid;
        }

        // A raw UIElement reports whatever it likes: the way a spanning child's distributed
        // minimum reaches a track without FrameworkElement's clamp.
        private sealed class UnclampedElement : UIElement
        {
            private readonly double _height;

            public UnclampedElement(double height)
            {
                _height = height;
            }

            protected override Size MeasureCore(Size availableSize)
            {
                return new Size(0, _height);
            }
        }

        private static void RunOnStaThread(Action action)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (failure != null)
            {
                throw new AssertFailedException(failure.Message, failure);
            }
        }
    }
}
