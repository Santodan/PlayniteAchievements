using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Tests.Models.Settings
{
    [TestClass]
    public class ProviderNotificationOverrideTests
    {
        private static ProviderNotificationOverride MakeOverride(
            bool? unlockToasts = null,
            bool? friendUnlockToasts = null,
            bool? screenshotClean = null,
            bool? screenshotWithToast = null,
            bool? screenshotFramed = null,
            bool? recordings = null)
        {
            return new ProviderNotificationOverride
            {
                UnlockToasts = unlockToasts,
                FriendUnlockToasts = friendUnlockToasts,
                ScreenshotClean = screenshotClean,
                ScreenshotWithToast = screenshotWithToast,
                ScreenshotFramed = screenshotFramed,
                Recordings = recordings
            };
        }

        [TestMethod]
        public void IsAllInherit_TrueOnlyWhenEveryValueIsNull()
        {
            Assert.IsTrue(new ProviderNotificationOverride().IsAllInherit);
            Assert.IsFalse(MakeOverride(unlockToasts: true).IsAllInherit);
            Assert.IsFalse(MakeOverride(friendUnlockToasts: false).IsAllInherit);
            Assert.IsFalse(MakeOverride(screenshotClean: true).IsAllInherit);
            Assert.IsFalse(MakeOverride(screenshotWithToast: false).IsAllInherit);
            Assert.IsFalse(MakeOverride(screenshotFramed: true).IsAllInherit);
            Assert.IsFalse(MakeOverride(recordings: false).IsAllInherit);
        }

        [TestMethod]
        public void SetProviderNotificationOverride_StoresCloneAndReadsBackCaseInsensitively()
        {
            var settings = new PersistedSettings();
            var value = MakeOverride(unlockToasts: false, recordings: true);

            settings.SetProviderNotificationOverride("Steam", value);

            var stored = settings.GetProviderNotificationOverride("sTeAm");
            Assert.IsNotNull(stored);
            Assert.AreNotSame(value, stored);
            Assert.AreEqual(false, stored.UnlockToasts);
            Assert.AreEqual(true, stored.Recordings);

            // Mutating the instance passed to the setter must not affect the stored clone.
            value.UnlockToasts = true;
            Assert.AreEqual(false, settings.GetProviderNotificationOverride("Steam").UnlockToasts);
        }

        [TestMethod]
        public void SetProviderNotificationOverride_AllInheritRemovesTheEntry()
        {
            var settings = new PersistedSettings();
            settings.SetProviderNotificationOverride("Steam", MakeOverride(unlockToasts: false));
            Assert.IsNotNull(settings.GetProviderNotificationOverride("Steam"));

            settings.SetProviderNotificationOverride("Steam", new ProviderNotificationOverride());

            Assert.IsNull(settings.GetProviderNotificationOverride("Steam"));
            Assert.AreEqual(0, settings.ProviderNotificationOverrides.Count);
        }

        [TestMethod]
        public void SetProviderNotificationOverride_NullRemovesTheEntry()
        {
            var settings = new PersistedSettings();
            settings.SetProviderNotificationOverride("Steam", MakeOverride(screenshotFramed: true));

            settings.SetProviderNotificationOverride("Steam", null);

            Assert.IsNull(settings.GetProviderNotificationOverride("Steam"));
        }

        [TestMethod]
        public void SetProviderNotificationOverride_TrimsProviderKeyAndIgnoresBlank()
        {
            var settings = new PersistedSettings();
            settings.SetProviderNotificationOverride("  Steam  ", MakeOverride(unlockToasts: true));
            settings.SetProviderNotificationOverride("   ", MakeOverride(unlockToasts: true));
            settings.SetProviderNotificationOverride(null, MakeOverride(unlockToasts: true));

            Assert.AreEqual(1, settings.ProviderNotificationOverrides.Count);
            Assert.IsNotNull(settings.GetProviderNotificationOverride("Steam"));
        }

        [TestMethod]
        public void ProviderNotificationOverrides_AssignmentDropsBlankKeysAndAllInheritEntries()
        {
            var settings = new PersistedSettings
            {
                ProviderNotificationOverrides = new Dictionary<string, ProviderNotificationOverride>
                {
                    ["Steam"] = MakeOverride(unlockToasts: false),
                    ["  "] = MakeOverride(unlockToasts: true),
                    ["Epic"] = new ProviderNotificationOverride(),
                    ["GOG"] = null
                }
            };

            Assert.AreEqual(1, settings.ProviderNotificationOverrides.Count);
            Assert.IsNotNull(settings.GetProviderNotificationOverride("Steam"));
            Assert.IsNull(settings.GetProviderNotificationOverride("Epic"));
            Assert.IsNull(settings.GetProviderNotificationOverride("GOG"));
        }

        [TestMethod]
        public void ProviderNotificationOverrides_AssignmentIsCaseInsensitiveKeyed()
        {
            var settings = new PersistedSettings
            {
                ProviderNotificationOverrides = new Dictionary<string, ProviderNotificationOverride>
                {
                    ["Steam"] = MakeOverride(unlockToasts: false)
                }
            };

            Assert.IsTrue(settings.ProviderNotificationOverrides.ContainsKey("STEAM"));
        }

        [TestMethod]
        public void Clone_DeepCopiesOverrides()
        {
            var source = new PersistedSettings();
            source.SetProviderNotificationOverride("Steam", MakeOverride(unlockToasts: false, screenshotClean: true));

            var clone = source.Clone();

            // Mutating the source after cloning must not leak into the clone.
            source.GetProviderNotificationOverride("Steam").UnlockToasts = true;
            source.SetProviderNotificationOverride("Epic", MakeOverride(recordings: false));

            Assert.AreEqual(1, clone.ProviderNotificationOverrides.Count);
            var cloned = clone.GetProviderNotificationOverride("Steam");
            Assert.AreEqual(false, cloned.UnlockToasts);
            Assert.AreEqual(true, cloned.ScreenshotClean);
            Assert.IsNull(cloned.FriendUnlockToasts);
        }

        [TestMethod]
        public void CopyFrom_DeepCopiesOverrides()
        {
            var source = new PersistedSettings();
            source.SetProviderNotificationOverride("Steam", MakeOverride(friendUnlockToasts: true, screenshotFramed: false));

            var target = new PersistedSettings();
            target.SetProviderNotificationOverride("Epic", MakeOverride(recordings: false));
            target.CopyFrom(source);

            // The copy replaces target state and is isolated from later source mutations.
            Assert.IsNull(target.GetProviderNotificationOverride("Epic"));
            source.GetProviderNotificationOverride("Steam").FriendUnlockToasts = false;

            var copied = target.GetProviderNotificationOverride("Steam");
            Assert.IsNotNull(copied);
            Assert.AreEqual(true, copied.FriendUnlockToasts);
            Assert.AreEqual(false, copied.ScreenshotFramed);
        }

        [TestMethod]
        public void JsonRoundTrip_PreservesNullableValuesAndSkipsComputedFlag()
        {
            var value = MakeOverride(
                unlockToasts: true,
                friendUnlockToasts: false,
                recordings: false);

            var json = JsonConvert.SerializeObject(value);
            Assert.IsFalse(json.Contains(nameof(ProviderNotificationOverride.IsAllInherit)));

            var roundTripped = JsonConvert.DeserializeObject<ProviderNotificationOverride>(json);
            Assert.AreEqual(true, roundTripped.UnlockToasts);
            Assert.AreEqual(false, roundTripped.FriendUnlockToasts);
            Assert.IsNull(roundTripped.ScreenshotClean);
            Assert.IsNull(roundTripped.ScreenshotWithToast);
            Assert.IsNull(roundTripped.ScreenshotFramed);
            Assert.AreEqual(false, roundTripped.Recordings);
        }

        [TestMethod]
        public void JsonRoundTrip_DictionaryKeepsOnlyDeviatingProvidersAfterNormalization()
        {
            var settings = new PersistedSettings();
            settings.SetProviderNotificationOverride("Steam", MakeOverride(unlockToasts: false));

            var json = JsonConvert.SerializeObject(settings.ProviderNotificationOverrides);
            var restored = new PersistedSettings
            {
                ProviderNotificationOverrides =
                    JsonConvert.DeserializeObject<Dictionary<string, ProviderNotificationOverride>>(json)
            };

            Assert.AreEqual(1, restored.ProviderNotificationOverrides.Count);
            Assert.AreEqual(false, restored.GetProviderNotificationOverride("steam").UnlockToasts);
        }
    }
}
