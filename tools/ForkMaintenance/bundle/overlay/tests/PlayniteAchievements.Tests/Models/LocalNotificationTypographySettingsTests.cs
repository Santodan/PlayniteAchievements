using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using PlayniteAchievements.Providers.Local;

namespace PlayniteAchievements.Tests.Models
{
    [TestClass]
    public class LocalNotificationTypographySettingsTests
    {
        [TestMethod]
        public void TypographySettings_DefaultAndNormalizeFontFamilies()
        {
            var settings = new LocalSettings();

            Assert.AreEqual("Default", settings.OverlayCustomLine1FontFamily);
            Assert.AreEqual(LocalOverlayTextFormattingMode.Auto, settings.OverlayCustomTextFormattingMode);

            settings.OverlayCustomLine1FontFamily = "  Segoe UI  ";
            settings.OverlayCustomLine2FontFamily = " ";
            settings.OverlayCustomTextFormattingMode = LocalOverlayTextFormattingMode.Ideal;

            Assert.AreEqual("Segoe UI", settings.OverlayCustomLine1FontFamily);
            Assert.AreEqual("Default", settings.OverlayCustomLine2FontFamily);
            Assert.AreEqual(LocalOverlayTextFormattingMode.Ideal, settings.OverlayCustomTextFormattingMode);
        }

        [TestMethod]
        public void TypographySettings_StyleSlotRoundTripsThroughJsonAndNormalization()
        {
            var source = new LocalCustomOverlayStyleSlot
            {
                Line1FontFamily = "Segoe UI",
                Line2FontFamily = "Titillium Web",
                Line6FontFamily = "JetBrains Mono",
                TextFormattingMode = LocalOverlayTextFormattingMode.Display
            };

            var json = JsonConvert.SerializeObject(source);
            var restored = JsonConvert.DeserializeObject<LocalCustomOverlayStyleSlot>(json);
            var settings = new LocalSettings
            {
                CustomOverlayStyleSlots = new List<LocalCustomOverlayStyleSlot> { restored }
            };

            var slot = settings.CustomOverlayStyleSlots[0];
            Assert.AreEqual("Segoe UI", slot.Line1FontFamily);
            Assert.AreEqual("Titillium Web", slot.Line2FontFamily);
            Assert.AreEqual("Default", slot.Line3FontFamily);
            Assert.AreEqual("JetBrains Mono", slot.Line6FontFamily);
            Assert.AreEqual(LocalOverlayTextFormattingMode.Display, slot.TextFormattingMode);
        }
    }
}
