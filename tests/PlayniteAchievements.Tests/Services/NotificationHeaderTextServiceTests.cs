using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.UI;

namespace PlayniteAchievements.Tests.Services
{
    [TestClass]
    public class NotificationHeaderTextServiceTests
    {
        private const string EnglishXaml = @"<ResourceDictionary
    xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
    xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml""
    xmlns:sys=""clr-namespace:System;assembly=mscorlib"">
    <sys:String x:Key=""LOCPlayAch_Toast_AchievementUnlocked"">Achievement unlocked</sys:String>
    <sys:String x:Key=""LOCPlayAch_Toast_FriendUnlocked"">{0} unlocked</sys:String>
    <sys:String x:Key=""LOCPlayAch_Toast_Congratulations"">Congratulations!</sys:String>
    <sys:String x:Key=""LOCPlayAch_Toast_CompletedTheGame"">completed the game!</sys:String>
</ResourceDictionary>";

        private const string GermanXaml = @"<ResourceDictionary
    xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
    xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml""
    xmlns:sys=""clr-namespace:System;assembly=mscorlib"">
    <sys:String x:Key=""LOCPlayAch_Toast_AchievementUnlocked"">Erfolg freigeschaltet</sys:String>
    <sys:String x:Key=""LOCPlayAch_Toast_CompletedTheGame"">hat das Spiel abgeschlossen!</sys:String>
</ResourceDictionary>";

        private string _tempDirectory;

        [TestInitialize]
        public void Initialize()
        {
            _tempDirectory = Path.Combine(Path.GetTempPath(), "PlayAchTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDirectory);
            File.WriteAllText(Path.Combine(_tempDirectory, "en_US.xaml"), EnglishXaml);
            File.WriteAllText(Path.Combine(_tempDirectory, "de_DE.xaml"), GermanXaml);
        }

        [TestCleanup]
        public void Cleanup()
        {
            try
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
            catch
            {
            }
        }

        [TestMethod]
        public void NormalizeForStore_BlankOrDefaultBecomesNull()
        {
            Assert.IsNull(NotificationHeaderTextService.NormalizeForStore(null, "Achievement unlocked"));
            Assert.IsNull(NotificationHeaderTextService.NormalizeForStore("   ", "Achievement unlocked"));
            Assert.IsNull(NotificationHeaderTextService.NormalizeForStore(
                "  Achievement unlocked ", "Achievement unlocked"));
            Assert.AreEqual("Ding!", NotificationHeaderTextService.NormalizeForStore(
                " Ding! ", "Achievement unlocked"));
        }

        [TestMethod]
        public void IsValidHeaderFormat_RequiresPlaceholderAndFormatability()
        {
            Assert.IsTrue(NotificationHeaderTextService.IsValidHeaderFormat("{0} unlocked"));
            Assert.IsFalse(NotificationHeaderTextService.IsValidHeaderFormat(null));
            Assert.IsFalse(NotificationHeaderTextService.IsValidHeaderFormat("   "));
            Assert.IsFalse(NotificationHeaderTextService.IsValidHeaderFormat("someone unlocked"));
            Assert.IsFalse(NotificationHeaderTextService.IsValidHeaderFormat("{0} unlocked {1"));
        }

        [TestMethod]
        public void Relocalize_NormalizesShippedDefaultsFromAnyLanguageToNull()
        {
            var service = new NotificationHeaderTextService(_tempDirectory);
            var settings = new PersistedSettings();
            settings.NotificationStyle.HeaderTexts.UnlockHeader = "Erfolg freigeschaltet";
            settings.NotificationStyle.HeaderTexts.FriendUnlockHeaderFormat = "{0} unlocked";
            settings.NotificationStyle.HeaderTexts.CompletionHeader = "My own header";
            settings.NotificationStyle.HeaderTexts.FriendCompletionHeaderFormat =
                "{0} hat das Spiel abgeschlossen!";

            var changed = service.RelocalizeDefaultHeaderTexts(settings);

            Assert.IsTrue(changed);
            Assert.IsNull(settings.NotificationStyle.HeaderTexts.UnlockHeader);
            Assert.IsNull(settings.NotificationStyle.HeaderTexts.FriendUnlockHeaderFormat);
            Assert.IsNull(settings.NotificationStyle.HeaderTexts.FriendCompletionHeaderFormat);
            // User-customized text sticks.
            Assert.AreEqual("My own header", settings.NotificationStyle.HeaderTexts.CompletionHeader);
        }

        [TestMethod]
        public void Relocalize_CoversProviderStyleCopies()
        {
            var service = new NotificationHeaderTextService(_tempDirectory);
            var settings = new PersistedSettings();
            var providerStyle = NotificationStyleSettings.CreateDefault();
            providerStyle.HeaderTexts.UnlockHeader = "Achievement unlocked";
            providerStyle.HeaderTexts.CompletionHeader = "Custom congrats";
            settings.SetProviderNotificationStyle("Steam", providerStyle);

            var changed = service.RelocalizeDefaultHeaderTexts(settings);

            Assert.IsTrue(changed);
            var stored = settings.GetProviderNotificationStyle("Steam");
            Assert.IsNull(stored.HeaderTexts.UnlockHeader);
            Assert.AreEqual("Custom congrats", stored.HeaderTexts.CompletionHeader);
        }

        [TestMethod]
        public void Relocalize_NoChangeReturnsFalse()
        {
            var service = new NotificationHeaderTextService(_tempDirectory);
            var settings = new PersistedSettings();
            settings.NotificationStyle.HeaderTexts.UnlockHeader = "Totally custom";

            Assert.IsFalse(service.RelocalizeDefaultHeaderTexts(settings));
            Assert.IsFalse(service.RelocalizeDefaultHeaderTexts(null));
            Assert.AreEqual("Totally custom", settings.NotificationStyle.HeaderTexts.UnlockHeader);
        }
    }
}
