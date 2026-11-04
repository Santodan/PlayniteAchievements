using System.Globalization;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Views.Converters;

namespace PlayniteAchievements.Tests.Views
{
    [TestClass]
    public class NullableNumericTextConverterTests
    {
        private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");
        private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

        private static double? Parse(string text, CultureInfo culture)
        {
            return NullableNumericTextHelper.TryParseDouble(text, culture, out var value) ? value : (double?)null;
        }

        [TestMethod]
        public void German_AcceptsCommaAndDotDecimals()
        {
            Assert.AreEqual(0.5, Parse("0,5", German));
            Assert.AreEqual(0.5, Parse("0.5", German));
            Assert.AreEqual(28.5, Parse("28,5", German));
            Assert.AreEqual(28.5, Parse("28.5", German));
        }

        [TestMethod]
        public void English_AcceptsDotAndRejectsComma()
        {
            Assert.AreEqual(0.5, Parse("0.5", English));
            Assert.AreEqual(28.5, Parse("28.5", English));
            Assert.IsNull(Parse("28,5", English));
        }
    }
}
