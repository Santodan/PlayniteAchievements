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
        public void ClampedGrid_NeverAsksForMoreThanItsSlotWhenAChildOvershootsATrack()
        {
            RunOnStaThread(() =>
            {
                var grid = new ClampedDesiredSizeGrid();
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var oversized = new Border { Height = 80 };
                Grid.SetRow(oversized, 0);
                grid.Children.Add(oversized);

                grid.Measure(new Size(200, 100));

                Assert.AreEqual(100, grid.DesiredSize.Height, "a plain Grid would ask for 80 + 50");
                Assert.AreEqual(200, grid.DesiredSize.Width);

                var plain = new Grid();
                plain.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                plain.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                var control = new Border { Height = 80 };
                Grid.SetRow(control, 0);
                plain.Children.Add(control);
                plain.Measure(new Size(200, 100));
                Assert.IsTrue(plain.DesiredSize.Height > 100, "the mechanism the clamp guards against");
            });
        }

        [TestMethod]
        public void OverlayHost_KeepsAnOversizedHandleOutOfTheGridMeasure()
        {
            RunOnStaThread(() =>
            {
                var grid = new Grid();
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                var handle = new Border { Height = 80 };
                Grid.SetRow(handle, 0);
                grid.Children.Add(OverlayLayoutHost.Wrap(handle));

                grid.Measure(new Size(200, 100));

                Assert.AreEqual(0, grid.DesiredSize.Height);
            });
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
