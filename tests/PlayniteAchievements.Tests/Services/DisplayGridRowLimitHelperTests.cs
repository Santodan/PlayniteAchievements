using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services;

namespace PlayniteAchievements.Tests.Services
{
    [TestClass]
    public class DisplayGridRowLimitHelperTests
    {
        [TestMethod]
        public void Limit_NullMaxRowsReturnsAllItems()
        {
            var items = Enumerable.Range(1, 5).ToList();

            var result = DisplayGridRowLimitHelper.Limit(items, null);

            CollectionAssert.AreEqual(items, result);
        }

        [TestMethod]
        public void Limit_PositiveMaxRowsReturnsPrefix()
        {
            var items = Enumerable.Range(1, 5).ToList();

            var result = DisplayGridRowLimitHelper.Limit(items, 3);

            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, result);
        }

        [TestMethod]
        public void Limit_NonPositiveMaxRowsReturnsAllItems()
        {
            var items = Enumerable.Range(1, 5).ToList();

            var zeroResult = DisplayGridRowLimitHelper.Limit(items, 0);
            var negativeResult = DisplayGridRowLimitHelper.Limit(items, -2);

            CollectionAssert.AreEqual(items, zeroResult);
            CollectionAssert.AreEqual(items, negativeResult);
        }
    }
}
