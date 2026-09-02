using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Common;
using PlayniteAchievements.Views.Converters;

namespace PlayniteAchievements.Tests.Views
{
    [TestClass]
    [DoNotParallelize]
    public class WholePercentToTextConverterTests
    {
        private readonly WholePercentToTextConverter _converter = new WholePercentToTextConverter();

        [TestInitialize]
        public void PinFormattingCulture()
        {
            FormattingCulture.Initialize(() => "english");
        }

        [TestMethod]
        public void Convert_NearCompleteDoubleRendersNinetyNine()
        {
            Assert.AreEqual("99%", _converter.Convert(99.5, null, null, null));
            Assert.AreEqual("99%", _converter.Convert(99.01, null, null, null));
        }

        [TestMethod]
        public void Convert_FullValueRendersOneHundred()
        {
            Assert.AreEqual("100%", _converter.Convert(100.0, null, null, null));
            Assert.AreEqual("100%", _converter.Convert(100, null, null, null));
        }

        [TestMethod]
        public void Convert_OtherDoublesRoundMidpointAwayFromZero()
        {
            Assert.AreEqual("13%", _converter.Convert(12.5, null, null, null));
            Assert.AreEqual("0%", _converter.Convert(0.4, null, null, null));
        }

        [TestMethod]
        public void Convert_UnsupportedValueIsEmpty()
        {
            Assert.AreEqual(string.Empty, _converter.Convert("text", null, null, null));
            Assert.AreEqual(string.Empty, _converter.Convert(null, null, null, null));
        }
    }
}
