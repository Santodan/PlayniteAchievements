using System;
using System.Globalization;
using System.Windows;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Views.Converters;

namespace PlayniteAchievements.Tests.Views
{
    /// <summary>
    /// Pins the gating the Captures page relies on: the capture delay row is enabled when either
    /// the screenshot or the recording master switch is on, and disabled only when both are off.
    /// </summary>
    [TestClass]
    public class BooleanAggregateConverterTests
    {
        private static object Convert(BooleanAggregateMode mode, params object[] values)
        {
            return new BooleanAggregateConverter { Mode = mode }
                .Convert(values, typeof(bool), null, CultureInfo.InvariantCulture);
        }

        [TestMethod]
        public void Any_OneMasterOnEnablesTheRow()
        {
            Assert.AreEqual(true, Convert(BooleanAggregateMode.Any, true, false));
            Assert.AreEqual(true, Convert(BooleanAggregateMode.Any, false, true));
            Assert.AreEqual(true, Convert(BooleanAggregateMode.Any, true, true));
        }

        [TestMethod]
        public void Any_BothMastersOffDisablesTheRow()
        {
            Assert.AreEqual(false, Convert(BooleanAggregateMode.Any, false, false));
        }

        [TestMethod]
        public void All_RequiresEveryInput()
        {
            Assert.AreEqual(true, Convert(BooleanAggregateMode.All, true, true));
            Assert.AreEqual(false, Convert(BooleanAggregateMode.All, true, false));
        }

        [TestMethod]
        public void UnresolvedBindingsCountAsFalse()
        {
            Assert.AreEqual(false, Convert(BooleanAggregateMode.Any, DependencyProperty.UnsetValue, null));
            Assert.AreEqual(false, Convert(BooleanAggregateMode.All, true, DependencyProperty.UnsetValue));
            Assert.AreEqual(false, Convert(BooleanAggregateMode.Any));
        }

        [TestMethod]
        public void ConvertBackIsNotSupported()
        {
            Assert.ThrowsException<NotSupportedException>(() =>
                new BooleanAggregateConverter().ConvertBack(
                    true, new[] { typeof(bool) }, null, CultureInfo.InvariantCulture));
        }
    }
}
