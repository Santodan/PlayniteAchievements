using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Views.Controls;

namespace PlayniteAchievements.Tests.Views
{
    [TestClass]
    public class ClampedDesiredSizeDecoratorTests
    {
        [TestMethod]
        public void Measure_NeverReportsMoreThanTheFiniteConstraint()
        {
            RunOnStaThread(() =>
            {
                var decorator = new ClampedDesiredSizeDecorator
                {
                    Child = new Border { Width = 1000, Height = 1000 }
                };

                decorator.Measure(new Size(300, 200));

                Assert.AreEqual(300, decorator.DesiredSize.Width);
                Assert.AreEqual(200, decorator.DesiredSize.Height);
            });
        }

        [TestMethod]
        public void Measure_PassesASmallerChildAndAnInfiniteConstraintThrough()
        {
            RunOnStaThread(() =>
            {
                var decorator = new ClampedDesiredSizeDecorator
                {
                    Child = new Border { Width = 40, Height = 30 }
                };

                decorator.Measure(new Size(300, 200));
                Assert.AreEqual(40, decorator.DesiredSize.Width);
                Assert.AreEqual(30, decorator.DesiredSize.Height);

                decorator.Child = new Border { Width = 1000, Height = 1000 };
                decorator.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Assert.AreEqual(1000, decorator.DesiredSize.Width);
                Assert.AreEqual(1000, decorator.DesiredSize.Height);
            });
        }

        [TestMethod]
        public void Arrange_GivesTheChildTheWholeSlot()
        {
            RunOnStaThread(() =>
            {
                var child = new Border { Width = 1000, Height = 1000 };
                var decorator = new ClampedDesiredSizeDecorator { Child = child };

                decorator.Measure(new Size(300, 200));
                decorator.Arrange(new Rect(0, 0, 300, 200));

                Assert.AreEqual(300, decorator.RenderSize.Width);
                Assert.AreEqual(200, decorator.RenderSize.Height);
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
