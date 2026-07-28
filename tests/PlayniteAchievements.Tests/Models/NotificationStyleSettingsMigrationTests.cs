using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Tests.Models
{
    [TestClass]
    public class NotificationStyleSettingsMigrationTests
    {
        [TestMethod]
        public void MigrateFromJson_MovesFlatFlagsIntoNotificationStyleAndRemovesThem()
        {
            var json = @"{""Persisted"":{
                ""ToastShowHeader"":false,
                ""ToastShowUnlockTime"":true,
                ""FrameShowDescription"":false,
                ""FrameRarityColoredName"":false,
                ""EnableNotifications"":true}}";

            var migrated = NotificationStyleSettingsMigration.MigrateFromJson(json);

            var persisted = (JObject)JObject.Parse(migrated)["Persisted"];
            var style = (JObject)persisted["NotificationStyle"];
            Assert.IsNotNull(style);

            var toast = (JObject)style["Toast"];
            Assert.IsFalse(toast.Value<bool>("ShowHeader"));
            Assert.IsTrue(toast.Value<bool>("ShowUnlockTime"));
            // Absent flat values fall back to the legacy defaults.
            Assert.IsTrue(toast.Value<bool>("ShowDescription"));
            Assert.IsTrue(toast.Value<bool>("RarityColoredName"));

            var frame = (JObject)style["Frame"];
            Assert.IsFalse(frame.Value<bool>("ShowDescription"));
            Assert.IsFalse(frame.Value<bool>("RarityColoredName"));
            Assert.IsTrue(frame.Value<bool>("ShowHeader"));
            // The frame's unlock-time default is true, unlike the toast's.
            Assert.IsTrue(frame.Value<bool>("ShowUnlockTime"));

            Assert.IsNull(persisted["ToastShowHeader"]);
            Assert.IsNull(persisted["FrameShowDescription"]);
            Assert.IsNotNull(persisted["EnableNotifications"]);
        }

        [TestMethod]
        public void MigrateFromJson_MigratedConfigDeserializesWithValuesApplied()
        {
            var json = @"{""Persisted"":{""ToastShowGameName"":false,""FrameShowRarityBadge"":false}}";

            var migrated = NotificationStyleSettingsMigration.MigrateFromJson(json);
            var persisted = JObject.Parse(migrated)["Persisted"]
                .ToObject<PersistedSettings>();

            Assert.IsFalse(persisted.NotificationStyle.Toast.ShowGameName);
            Assert.IsFalse(persisted.NotificationStyle.Frame.ShowRarityBadge);
            Assert.IsTrue(persisted.NotificationStyle.Toast.ShowHeader);
        }

        [TestMethod]
        public void MigrateFromJson_NoOpWhenNotificationStyleAlreadyExists()
        {
            var json = @"{""Persisted"":{
                ""NotificationStyle"":{""Toast"":{""ShowHeader"":false}},
                ""ToastShowHeader"":true}}";

            Assert.AreSame(json, NotificationStyleSettingsMigration.MigrateFromJson(json));
        }

        [TestMethod]
        public void MigrateFromJson_NoOpWhenNoFlatFlagsPresent()
        {
            var json = @"{""Persisted"":{""EnableNotifications"":true}}";

            Assert.AreSame(json, NotificationStyleSettingsMigration.MigrateFromJson(json));
        }

        [TestMethod]
        public void MigrateFromJson_IsIdempotent()
        {
            var json = @"{""Persisted"":{""ToastShowHeader"":false}}";

            var once = NotificationStyleSettingsMigration.MigrateFromJson(json);
            var twice = NotificationStyleSettingsMigration.MigrateFromJson(once);

            Assert.AreEqual(once, twice);
        }

        [TestMethod]
        public void MigrateFromJson_PassesThroughMalformedOrIrrelevantJson()
        {
            Assert.IsNull(NotificationStyleSettingsMigration.MigrateFromJson(null));
            Assert.AreEqual(string.Empty, NotificationStyleSettingsMigration.MigrateFromJson(string.Empty));
            Assert.AreEqual("not json", NotificationStyleSettingsMigration.MigrateFromJson("not json"));
            var noPersisted = @"{""Other"":1}";
            Assert.AreSame(noPersisted, NotificationStyleSettingsMigration.MigrateFromJson(noPersisted));
        }
    }
}
