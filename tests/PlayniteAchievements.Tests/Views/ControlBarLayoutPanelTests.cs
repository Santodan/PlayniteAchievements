using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Tests.TestInfrastructure;
using PlayniteAchievements.Views.Controls;

namespace PlayniteAchievements.Tests.Views
{
    [TestClass]
    public class ControlBarLayoutPanelTests
    {
        [TestMethod]
        public void OneLine_SearchFillsTheRestAndFiltersSitRight()
        {
            StaTestHost.Run(() =>
            {
                var (panel, leading, search, items) = Create(wrap: true, withSearch: true);

                Layout(panel, 600);

                Assert.AreEqual(30, panel.DesiredSize.Height);
                Assert.AreEqual(new Rect(40, 0, 360, 30), LayoutRect(search));
                Assert.AreEqual(new Rect(400, 0, 200, 30), LayoutRect(items));
                Assert.AreEqual(0, LayoutRect(leading).X);
            });
        }

        [TestMethod]
        public void Narrow_WithWrap_MovesFiltersBelowTheSearchBox()
        {
            StaTestHost.Run(() =>
            {
                var (panel, _, search, items) = Create(wrap: true, withSearch: true);

                Layout(panel, 300);

                Assert.AreEqual(30 + 4 + 30, panel.DesiredSize.Height);
                Assert.AreEqual(new Rect(40, 0, 260, 30), LayoutRect(search));
                Assert.AreEqual(new Rect(0, 34, 300, 30), LayoutRect(items));
            });
        }

        [TestMethod]
        public void Narrow_WithoutWrap_StaysOnOneLine()
        {
            StaTestHost.Run(() =>
            {
                var (panel, _, search, items) = Create(wrap: false, withSearch: true);

                Layout(panel, 300);

                Assert.AreEqual(30, panel.DesiredSize.Height);
                Assert.AreEqual(0, LayoutRect(items).Y);
                Assert.AreEqual(0, LayoutRect(search).Y);
            });
        }

        [TestMethod]
        public void NoSearch_Wrapped_FiltersStartAtTheTopWithNoGap()
        {
            StaTestHost.Run(() =>
            {
                var (panel, _, _, items) = Create(wrap: true, withSearch: false, leadingWidth: 0);

                Layout(panel, 150);

                Assert.AreEqual(0, LayoutRect(items).Y);
                Assert.AreEqual(150, LayoutRect(items).Width);
            });
        }

        [TestMethod]
        public void NoSearch_Wrapped_TakesTheFullWidthEvenWhenItFits()
        {
            StaTestHost.Run(() =>
            {
                var (panel, _, _, items) = Create(wrap: true, withSearch: false, leadingWidth: 0);

                Layout(panel, 600);

                Assert.AreEqual(new Rect(0, 0, 600, 30), LayoutRect(items));
            });
        }

        [TestMethod]
        public void NoSearch_OneLine_FiltersFollowTheLeadingItems()
        {
            StaTestHost.Run(() =>
            {
                var (panel, _, _, items) = Create(wrap: false, withSearch: false);
                Layout(panel, 600);

                Assert.AreEqual(40, LayoutRect(items).X, "no empty search space before the filters");
            });
        }
        [TestMethod]
        public void CenteredWrapPanel_CentersEachLine()
        {
            StaTestHost.Run(() =>
            {
                var panel = new CenteredWrapPanel();
                var first = new Border { Width = 100, Height = 20 };
                var second = new Border { Width = 100, Height = 20 };
                var third = new Border { Width = 60, Height = 20 };
                panel.Children.Add(first);
                panel.Children.Add(second);
                panel.Children.Add(third);

                Layout(panel, 250);

                Assert.AreEqual(new Size(200, 40), panel.DesiredSize);
                Assert.AreEqual(new Rect(25, 0, 100, 20), LayoutRect(first));
                Assert.AreEqual(new Rect(125, 0, 100, 20), LayoutRect(second));
                Assert.AreEqual(new Rect(95, 20, 60, 20), LayoutRect(third));
            });
        }

        [TestMethod]
        public void CenteredWrapPanel_UnboundedWidthKeepsOneLine()
        {
            StaTestHost.Run(() =>
            {
                var panel = new CenteredWrapPanel();
                panel.Children.Add(new Border { Width = 100, Height = 20 });
                panel.Children.Add(new Border { Width = 100, Height = 24 });

                panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

                Assert.AreEqual(new Size(200, 24), panel.DesiredSize);
            });
        }

        [TestMethod]
        public void CenteredWrapPanel_ArrangeAFractionShortKeepsOneLine()
        {
            StaTestHost.Run(() =>
            {
                var panel = new CenteredWrapPanel();
                var last = new Border { Width = 60.3, Height = 20 };
                panel.Children.Add(new Border { Width = 100, Height = 20 });
                panel.Children.Add(new Border { Width = 100, Height = 20 });
                panel.Children.Add(last);

                panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                panel.Arrange(new Rect(0, 0, panel.DesiredSize.Width - 0.01, panel.DesiredSize.Height));

                Assert.AreEqual(0, LayoutRect(last).Y);
            });
        }

        private static (ControlBarLayoutPanel, FrameworkElement, ContentControl, FrameworkElement) Create(
            bool wrap,
            bool withSearch,
            double leadingWidth = 40)
        {
            var leading = new Border { Width = leadingWidth, Height = leadingWidth > 0 ? 30 : 0 };
            var search = new ContentControl
            {
                Content = withSearch ? new Border { Height = 30 } : null
            };
            var items = new Border { MinWidth = 0, Height = 30, Child = new Border { Width = 200 } };
            var panel = new ControlBarLayoutPanel { Wrap = wrap, SearchMinWidth = 100 };
            panel.Children.Add(leading);
            panel.Children.Add(search);
            panel.Children.Add(items);
            return (panel, leading, search, items);
        }

        private static void Layout(FrameworkElement panel, double width)
        {
            panel.Measure(new Size(width, double.PositiveInfinity));
            panel.Arrange(new Rect(0, 0, width, panel.DesiredSize.Height));
        }

        private static Rect LayoutRect(FrameworkElement element)
        {
            var offset = element.TranslatePoint(new Point(0, 0), (UIElement)element.Parent);
            return new Rect(offset, element.RenderSize);
        }
    }
}
