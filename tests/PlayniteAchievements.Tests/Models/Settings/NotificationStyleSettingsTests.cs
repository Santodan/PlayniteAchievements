using System.Collections.Generic;
using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.UI;

namespace PlayniteAchievements.Tests.Models.Settings
{
    [TestClass]
    public class NotificationStyleSettingsTests
    {
        [TestMethod]
        public void Defaults_MirrorTheLegacyFlatDefaults()
        {
            var style = NotificationStyleSettings.CreateDefault();

            Assert.IsTrue(style.Toast.ShowHeader);
            Assert.IsTrue(style.Toast.ShowRarityBadge);
            Assert.IsTrue(style.Toast.RarityColoredName);
            Assert.IsFalse(style.Toast.ShowUnlockTime);
            Assert.IsTrue(style.Frame.ShowUnlockTime);
            Assert.IsTrue(style.Toast.ShowProviderIcon);
            Assert.IsTrue(style.Frame.ShowProviderIcon);
            Assert.IsNull(style.Toast.LineOrder);
            Assert.IsNull(style.Toast.FontFamily);
            Assert.IsNull(style.Toast.HeaderFontSize);
            Assert.IsNull(style.ToastBackgroundImagePath);
            Assert.IsNull(style.BadgeImages.CommonPath);
            Assert.IsNull(style.HeaderTexts.UnlockHeader);

            var settings = new PersistedSettings();
            Assert.IsTrue(settings.ToastUseThemeStyling);
            Assert.IsTrue(settings.FrameUseThemeStyling);
            Assert.IsNotNull(settings.NotificationStyle);
            Assert.AreEqual(0, settings.ProviderNotificationStyles.Count);
        }

        [TestMethod]
        public void CanonicalizeLineOrder_NullOrEmptyYieldsDefaultOrder()
        {
            CollectionAssert.AreEqual(
                new List<string>(NotificationSurfaceStyle.DefaultLineOrder),
                NotificationSurfaceStyle.CanonicalizeLineOrder(null));
            CollectionAssert.AreEqual(
                new List<string>(NotificationSurfaceStyle.DefaultLineOrder),
                NotificationSurfaceStyle.CanonicalizeLineOrder(new string[0]));
        }

        [TestMethod]
        public void CanonicalizeLineOrder_DropsUnknownDeduplicatesAndAppendsMissing()
        {
            var result = NotificationSurfaceStyle.CanonicalizeLineOrder(new[]
            {
                "gamecategory",
                "Title",
                "NotALine",
                " title ",
                null
            });

            CollectionAssert.AreEqual(
                new List<string>
                {
                    NotificationSurfaceStyle.LineGameCategory,
                    NotificationSurfaceStyle.LineTitle,
                    NotificationSurfaceStyle.LineHeader,
                    NotificationSurfaceStyle.LineDescription
                },
                result);
        }

        [TestMethod]
        public void SetProviderNotificationStyle_StoresCloneAndReadsBackCaseInsensitively()
        {
            var settings = new PersistedSettings();
            var style = NotificationStyleSettings.CreateDefault();
            style.Toast.ShowDescription = false;
            style.BadgeImages.RarePath = @"c:\images\rare.gif";

            settings.SetProviderNotificationStyle("Steam", style);

            var stored = settings.GetProviderNotificationStyle("sTeAm");
            Assert.IsNotNull(stored);
            Assert.AreNotSame(style, stored);
            Assert.IsFalse(stored.Toast.ShowDescription);
            Assert.AreEqual(@"c:\images\rare.gif", stored.BadgeImages.RarePath);

            // Mutating the instance passed to the setter must not affect the stored clone.
            style.Toast.ShowDescription = true;
            Assert.IsFalse(settings.GetProviderNotificationStyle("Steam").Toast.ShowDescription);
        }

        [TestMethod]
        public void SetProviderNotificationStyle_NullRemovesTheEntry()
        {
            var settings = new PersistedSettings();
            settings.SetProviderNotificationStyle("Steam", NotificationStyleSettings.CreateDefault());
            Assert.IsNotNull(settings.GetProviderNotificationStyle("Steam"));

            settings.SetProviderNotificationStyle("Steam", null);

            Assert.IsNull(settings.GetProviderNotificationStyle("Steam"));
            Assert.AreEqual(0, settings.ProviderNotificationStyles.Count);
        }

        [TestMethod]
        public void SetProviderNotificationStyle_TrimsProviderKeyAndIgnoresBlank()
        {
            var settings = new PersistedSettings();
            settings.SetProviderNotificationStyle("  Steam  ", NotificationStyleSettings.CreateDefault());
            settings.SetProviderNotificationStyle("   ", NotificationStyleSettings.CreateDefault());
            settings.SetProviderNotificationStyle(null, NotificationStyleSettings.CreateDefault());

            Assert.AreEqual(1, settings.ProviderNotificationStyles.Count);
            Assert.IsNotNull(settings.GetProviderNotificationStyle("Steam"));
        }

        [TestMethod]
        public void ProviderNotificationStyles_AssignmentDropsBlankKeysAndNullValues()
        {
            var settings = new PersistedSettings
            {
                ProviderNotificationStyles = new Dictionary<string, NotificationStyleSettings>
                {
                    ["Steam"] = NotificationStyleSettings.CreateDefault(),
                    ["  "] = NotificationStyleSettings.CreateDefault(),
                    ["GOG"] = null
                }
            };

            Assert.AreEqual(1, settings.ProviderNotificationStyles.Count);
            Assert.IsTrue(settings.ProviderNotificationStyles.ContainsKey("STEAM"));
        }

        [TestMethod]
        public void CloneAndCopyFrom_DeepCopyStylesAndThemeFlags()
        {
            var source = new PersistedSettings
            {
                ToastUseThemeStyling = false,
                FrameUseThemeStyling = false
            };
            source.NotificationStyle.Toast.LineOrder = new List<string>
            {
                NotificationSurfaceStyle.LineTitle,
                NotificationSurfaceStyle.LineHeader
            };
            source.NotificationStyle.HeaderTexts.UnlockHeader = "Ding!";
            var providerStyle = NotificationStyleSettings.CreateDefault();
            providerStyle.Toast.FontFamily = "Consolas";
            source.SetProviderNotificationStyle("Steam", providerStyle);

            var clone = source.Clone();
            var target = new PersistedSettings();
            target.CopyFrom(source);

            foreach (var copy in new[] { clone, target })
            {
                Assert.IsFalse(copy.ToastUseThemeStyling);
                Assert.IsFalse(copy.FrameUseThemeStyling);
                Assert.AreNotSame(source.NotificationStyle, copy.NotificationStyle);
                Assert.AreEqual("Ding!", copy.NotificationStyle.HeaderTexts.UnlockHeader);
                CollectionAssert.AreEqual(
                    source.NotificationStyle.Toast.LineOrder,
                    copy.NotificationStyle.Toast.LineOrder);
                Assert.AreEqual("Consolas", copy.GetProviderNotificationStyle("Steam").Toast.FontFamily);
            }

            // Mutating the source after copying must not leak into the copies.
            source.NotificationStyle.HeaderTexts.UnlockHeader = "Changed";
            source.GetProviderNotificationStyle("Steam").Toast.FontFamily = "Arial";
            Assert.AreEqual("Ding!", clone.NotificationStyle.HeaderTexts.UnlockHeader);
            Assert.AreEqual("Consolas", target.GetProviderNotificationStyle("Steam").Toast.FontFamily);
        }

        [TestMethod]
        public void NotificationStyleResolver_PrefersProviderCopyThenDefault()
        {
            var settings = new PersistedSettings();
            settings.NotificationStyle.Toast.ShowDescription = false;
            var providerStyle = NotificationStyleSettings.CreateDefault();
            providerStyle.Toast.ShowGameName = false;
            settings.SetProviderNotificationStyle("Steam", providerStyle);

            var forSteam = NotificationStyleResolver.Resolve(settings, "Steam");
            Assert.IsFalse(forSteam.Toast.ShowGameName);
            Assert.IsTrue(forSteam.Toast.ShowDescription);

            var forOther = NotificationStyleResolver.Resolve(settings, "GOG");
            Assert.IsFalse(forOther.Toast.ShowDescription);

            Assert.IsNotNull(NotificationStyleResolver.Resolve(null, "Steam"));
        }

        [TestMethod]
        public void NotificationStyleResolver_GameSnapshotWinsThenReturnsToLiveProviderInheritance()
        {
            var tempDirectory = Path.Combine(
                Path.GetTempPath(),
                "PlayniteAchievementsTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);

            try
            {
                var settings = new PersistedSettings
                {
                    ToastUseThemeStyling = true,
                    FrameUseThemeStyling = true
                };
                settings.NotificationStyle.Toast.ShowHeader = false;
                var providerStyle = NotificationStyleSettings.CreateDefault();
                providerStyle.Toast.ShowHeader = true;
                settings.SetProviderNotificationStyle("Steam", providerStyle);

                var gameId = Guid.NewGuid();
                var store = new GameCustomDataStore(tempDirectory);
                var inherited = NotificationStyleResolver.ResolveAppearance(
                    settings,
                    "Steam",
                    gameId,
                    store);
                Assert.IsTrue(inherited.Style.Toast.ShowHeader);
                Assert.IsTrue(inherited.ToastUseThemeStyling);
                Assert.IsTrue(inherited.FrameUseThemeStyling);

                var gameStyle = providerStyle.Clone();
                gameStyle.Toast.ShowHeader = true;
                store.Save(gameId, new GameCustomDataFile
                {
                    PlayniteGameId = gameId,
                    NotificationAppearanceOverride = new GameNotificationAppearanceOverride
                    {
                        Style = gameStyle,
                        ToastUseThemeStyling = false,
                        FrameUseThemeStyling = false
                    }
                });

                settings.GetProviderNotificationStyle("Steam").Toast.ShowHeader = false;
                settings.ToastUseThemeStyling = true;
                settings.FrameUseThemeStyling = true;
                var overridden = NotificationStyleResolver.ResolveAppearance(
                    settings,
                    "Steam",
                    gameId,
                    store);
                Assert.IsTrue(overridden.Style.Toast.ShowHeader);
                Assert.IsFalse(overridden.ToastUseThemeStyling);
                Assert.IsFalse(overridden.FrameUseThemeStyling);

                store.Delete(gameId);
                var reverted = NotificationStyleResolver.ResolveAppearance(
                    settings,
                    "Steam",
                    gameId,
                    store);
                Assert.IsFalse(reverted.Style.Toast.ShowHeader);
                Assert.IsTrue(reverted.ToastUseThemeStyling);
                Assert.IsTrue(reverted.FrameUseThemeStyling);
            }
            finally
            {
                if (Directory.Exists(tempDirectory))
                {
                    Directory.Delete(tempDirectory, recursive: true);
                }
            }
        }
    }
}
