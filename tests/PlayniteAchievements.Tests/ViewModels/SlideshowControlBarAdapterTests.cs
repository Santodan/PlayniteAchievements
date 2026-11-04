using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;
using PlayniteAchievements.ViewModels.Showcase.Widgets;

namespace PlayniteAchievements.Tests.ViewModels
{
    [TestClass]
    public class SlideshowControlBarAdapterTests
    {
        [TestMethod]
        public void Variant_DefaultsToEveryStyleAndRoundTripsThroughState()
        {
            var adapter = new SlideshowControlBarAdapter();
            Assert.AreEqual(ShowcaseScreenshotVariant.All, adapter.Variant);
            Assert.IsNull(adapter.CaptureState().Variant);

            adapter.RestoreState(new ControlBarFilterState { Variant = "Framed", SearchText = "boss" });
            Assert.AreEqual(ShowcaseScreenshotVariant.Framed, adapter.Variant);

            var restored = new SlideshowControlBarAdapter();
            restored.RestoreState(adapter.CaptureState());
            Assert.AreEqual(ShowcaseScreenshotVariant.Framed, restored.Variant);
            Assert.AreEqual("boss", restored.SearchText);

            restored.RestoreState(new ControlBarFilterState { Variant = "junk" });
            Assert.AreEqual(ShowcaseScreenshotVariant.All, restored.Variant);
        }

        [TestMethod]
        public void StyleItem_PicksOneStyleAtATimeAndUnpickingPlaysEvery()
        {
            var adapter = new SlideshowControlBarAdapter();
            var changes = 0;
            System.Action listener = () => changes++;
            adapter.AddFilterListener(listener);
            var style = (GridMultiSelectFilter)adapter.ControlBar.Items
                .Single(item => item.Id == ShowcaseControlBarItems.ScreenshotStyle);

            style.SetSelected("Clean", true);
            style.SetSelected("Notification", true);
            Assert.AreEqual(ShowcaseScreenshotVariant.Notification, adapter.Variant);
            Assert.IsFalse(style.IsSelected("Clean"));

            style.SetSelected("Notification", false);
            Assert.AreEqual(ShowcaseScreenshotVariant.All, adapter.Variant);
            Assert.AreEqual(3, changes);
            System.GC.KeepAlive(listener);
        }
    }
}