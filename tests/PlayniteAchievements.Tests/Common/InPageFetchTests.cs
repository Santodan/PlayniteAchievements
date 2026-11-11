using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Common;

namespace PlayniteAchievements.Tests.Common
{
    [TestClass]
    public class InPageFetchTests
    {
        [TestMethod]
        public void Parse_SplitsStatusFromBody_AtTheFirstBar()
        {
            var result = InPageFetch.Parse("200|<html>a|b</html>");

            Assert.AreEqual(200, result.Status);
            Assert.AreEqual("<html>a|b</html>", result.Body);
            Assert.IsTrue(result.IsSuccess);
        }

        [TestMethod]
        public void Parse_KeepsErrorStatuses_AndAnEmptyBody()
        {
            var result = InPageFetch.Parse("404|");

            Assert.AreEqual(404, result.Status);
            Assert.AreEqual(string.Empty, result.Body);
            Assert.IsFalse(result.IsSuccess);
        }

        [TestMethod]
        public void Parse_ReadsAFailedOrMalformedValue_AsNoResult()
        {
            Assert.IsNull(InPageFetch.Parse("__ERR__"));
            Assert.IsNull(InPageFetch.Parse(null));
            Assert.IsNull(InPageFetch.Parse("<html>"));
            Assert.IsNull(InPageFetch.Parse("|body"));
        }

        [TestMethod]
        public void JsString_EscapesQuotesBackslashesAndLineBreaks()
        {
            Assert.AreEqual(@"'/game/It\'s-Here/a\\b\n'", InPageFetch.JsString(@"/game/It's-Here/a\b" + "\n"));
            Assert.AreEqual("'" + "\\" + "u2028'", InPageFetch.JsString(((char)0x2028).ToString()));
        }
    }
}
