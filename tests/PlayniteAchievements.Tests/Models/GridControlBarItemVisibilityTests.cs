using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.Tests.Models
{
    [TestClass]
    public class GridControlBarItemVisibilityTests
    {
        [TestMethod]
        public void HiddenItems_ToggleOnePerCall_AndEmptyMeansNone()
        {
            var options = new AchievementGridOptions();
            Assert.IsFalse(options.IsControlBarItemHidden(ShowcaseControlBarItems.Search));

            options.SetControlBarItemHidden(ShowcaseControlBarItems.Search, true);
            options.SetControlBarItemHidden(ShowcaseControlBarItems.Locked, true);
            Assert.IsTrue(options.IsControlBarItemHidden("search"), "ids match without regard to case");

            options.SetControlBarItemHidden(ShowcaseControlBarItems.Search, false);
            options.SetControlBarItemHidden(ShowcaseControlBarItems.Locked, false);
            Assert.IsNull(options.HiddenControlBarItems);
        }

        [TestMethod]
        public void HiddenItems_SurviveCloneAndJson()
        {
            var options = new AchievementGridOptions();
            options.SetControlBarItemHidden(ShowcaseControlBarItems.Tag, true);

            var clone = options.Clone();
            Assert.AreNotSame(options.HiddenControlBarItems, clone.HiddenControlBarItems);
            Assert.IsTrue(clone.IsControlBarItemHidden(ShowcaseControlBarItems.Tag));

            var json = Newtonsoft.Json.JsonConvert.SerializeObject(options);
            var read = Newtonsoft.Json.JsonConvert.DeserializeObject<AchievementGridOptions>(json);
            CollectionAssert.AreEqual(new[] { ShowcaseControlBarItems.Tag }, read.HiddenControlBarItems.ToArray());
        }

        [TestMethod]
        public void SingleGameBar_ItemsHaveIds_AndTogglesClearToShowingEverything()
        {
            var adapter = new AchievementGridControlBarAdapter();
            var ids = adapter.ControlBar.Items.Select(item => item.Id).ToList();
            CollectionAssert.AreEqual(
                new[]
                {
                    ShowcaseControlBarItems.Tag,
                    ShowcaseControlBarItems.Category,
                    ShowcaseControlBarItems.Compare,
                    ShowcaseControlBarItems.Unlocked,
                    ShowcaseControlBarItems.Locked,
                    ShowcaseControlBarItems.Hidden
                },
                ids);
            Assert.AreEqual(ShowcaseControlBarItems.Search, adapter.ControlBar.Search.Id);

            var locked = (GridToggleFilter)adapter.ControlBar.Items.Single(item => item.Id == ShowcaseControlBarItems.Locked);
            locked.IsChecked = false;
            locked.ClearSelection();
            Assert.IsTrue(locked.IsChecked);
        }
    }
}