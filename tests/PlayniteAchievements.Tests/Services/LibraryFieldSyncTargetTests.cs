using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Models.Tagging;
using PlayniteAchievements.Services.Tagging;

namespace PlayniteAchievements.Services.Tagging.Tests
{
    [TestClass]
    public class LibraryFieldSyncTargetTests
    {
        /// <summary>
        /// An in-memory field: items by id, and each game's id list read through
        /// <see cref="Game.TagIds"/> so the tests exercise the real null-list path.
        /// </summary>
        private sealed class FakeField : IManagedLibraryField
        {
            public readonly Dictionary<Guid, string> Items = new Dictionary<Guid, string>();
            public int NameLookups;

            public string Noun => "fake";

            public bool Exists(Guid id) => Items.ContainsKey(id);

            public Guid? FindByName(string name)
            {
                NameLookups++;
                return Items.FirstOrDefault(kvp => string.Equals(kvp.Value, name, StringComparison.OrdinalIgnoreCase)).Key is Guid id && id != Guid.Empty
                    ? id
                    : (Guid?)null;
            }

            public Guid? Create(string name)
            {
                var id = Guid.NewGuid();
                Items[id] = name;
                return id;
            }

            public bool Rename(Guid id, string newName)
            {
                if (!Items.TryGetValue(id, out var current) || current == newName)
                {
                    return false;
                }

                Items[id] = newName;
                return true;
            }

            public bool Remove(Guid id) => Items.Remove(id);

            public List<Guid> GetIds(Game game) => game.TagIds;

            public void SetIds(Game game, List<Guid> ids) => game.TagIds = ids;

            public Guid Add(string name)
            {
                var id = Guid.NewGuid();
                Items[id] = name;
                return id;
            }
        }

        private sealed class NullLogger : ILogger
        {
            public void Debug(string message) { }
            public void Debug(Exception exception, string message) { }
            public void Trace(string message) { }
            public void Trace(Exception exception, string message) { }
            public void Info(string message) { }
            public void Info(Exception exception, string message) { }
            public void Warn(string message) { }
            public void Warn(Exception exception, string message) { }
            public void Error(string message) { }
            public void Error(Exception exception, string message) { }
        }

        private static LibraryFieldSyncTarget Target(
            FakeField field,
            Dictionary<TagType, TagConfig> configs,
            IReadOnlyCollection<TagType> kinds = null,
            bool enabled = true)
        {
            return new LibraryFieldSyncTarget(field, new NullLogger(), () => configs, () => enabled, kinds, "LOCPlayAch_Tagging_RemovingProgress");
        }

        private static Dictionary<TagType, TagConfig> Configs(params (TagType Kind, string Name, bool Enabled)[] entries)
        {
            return entries.ToDictionary(
                entry => entry.Kind,
                entry => new TagConfig { DisplayName = entry.Name, IsEnabled = entry.Enabled });
        }

        [TestMethod]
        public void ResolveConfiguredId_AdoptsAnExistingItemByNameIgnoringCase_AndRemembersIt()
        {
            var field = new FakeField();
            var existing = field.Add("[pa] platform achievements");
            var configs = Configs((TagType.HasAchievements, "[PA] Platform Achievements", true));
            var target = Target(field, configs);

            var id = target.ResolveConfiguredId(TagType.HasAchievements, configs[TagType.HasAchievements]);

            Assert.AreEqual(existing, id);
            Assert.AreEqual(existing, configs[TagType.HasAchievements].TagId);
            Assert.AreEqual(1, field.Items.Count);
        }

        [TestMethod]
        public void ResolveConfiguredId_ReusesARememberedId_WithoutANameLookup()
        {
            var field = new FakeField();
            var remembered = field.Add("Old Name");
            var configs = Configs((TagType.HasAchievements, "New Name", true));
            configs[TagType.HasAchievements].TagId = remembered;
            var target = Target(field, configs);

            var id = target.ResolveConfiguredId(TagType.HasAchievements, configs[TagType.HasAchievements]);

            Assert.AreEqual(remembered, id);
            Assert.AreEqual(0, field.NameLookups);
        }

        [TestMethod]
        public void EnsureConfiguredIds_RenamesTheRememberedItem_ToTheConfiguredName()
        {
            var field = new FakeField();
            var remembered = field.Add("Old Name");
            var configs = Configs((TagType.HasAchievements, "New Name", true));
            configs[TagType.HasAchievements].TagId = remembered;
            var target = Target(field, configs);

            target.EnsureConfiguredIds();

            Assert.AreEqual(1, field.Items.Count);
            Assert.AreEqual("New Name", field.Items[remembered]);
            Assert.AreEqual(remembered, configs[TagType.HasAchievements].TagId);
        }

        [TestMethod]
        public void EnsureConfiguredIds_AppliesACaseOnlyRename()
        {
            var field = new FakeField();
            var remembered = field.Add("old name");
            var configs = Configs((TagType.HasAchievements, "Old Name", true));
            configs[TagType.HasAchievements].TagId = remembered;
            var target = Target(field, configs);

            target.EnsureConfiguredIds();

            Assert.AreEqual("Old Name", field.Items[remembered]);
        }

        [TestMethod]
        public void EnsureConfiguredIds_LeavesADisabledKindsItemUnrenamed()
        {
            var field = new FakeField();
            var remembered = field.Add("Old Name");
            var configs = Configs((TagType.HasAchievements, "New Name", false));
            configs[TagType.HasAchievements].TagId = remembered;
            var target = Target(field, configs);

            target.EnsureConfiguredIds();

            Assert.AreEqual("Old Name", field.Items[remembered]);
        }

        [TestMethod]
        public void ResolveConfiguredId_CreatesTheItem_WhenNeitherIdNorNameExists()
        {
            var field = new FakeField();
            var configs = Configs((TagType.CustomAchievements, "[PA] Custom Achievements", true));
            var target = Target(field, configs);

            var id = target.ResolveConfiguredId(TagType.CustomAchievements, configs[TagType.CustomAchievements]);

            Assert.IsTrue(id.HasValue);
            Assert.AreEqual("[PA] Custom Achievements", field.Items[id.Value]);
            Assert.AreEqual(id, configs[TagType.CustomAchievements].TagId);
        }

        [TestMethod]
        public void SyncGame_CreatesTheIdList_AddsEnabledApplicableKinds_AndStripsTheRemovalSet()
        {
            var field = new FakeField();
            var stale = field.Add("Stale");
            var foreign = field.Add("User's own");
            var configs = Configs(
                (TagType.HasAchievements, "Platform", true),
                (TagType.CustomAchievements, "Custom", false),
                (TagType.Completed, "Done", true));
            var target = Target(field, configs);
            var game = new Game { TagIds = null };

            var changed = target.SyncGame(game, new[] { TagType.HasAchievements, TagType.CustomAchievements }, new HashSet<Guid> { stale });

            Assert.IsTrue(changed);
            Assert.AreEqual(1, game.TagIds.Count);
            Assert.AreEqual(configs[TagType.HasAchievements].TagId, game.TagIds[0]);

            game.TagIds.Add(stale);
            game.TagIds.Add(foreign);
            changed = target.SyncGame(game, new[] { TagType.HasAchievements }, new HashSet<Guid> { stale });

            Assert.IsTrue(changed);
            CollectionAssert.AreEquivalent(new[] { configs[TagType.HasAchievements].TagId.Value, foreign }, game.TagIds);

            Assert.IsFalse(target.SyncGame(game, new[] { TagType.HasAchievements }, new HashSet<Guid> { stale }));
        }

        [TestMethod]
        public void SyncGame_ReportsNoChange_WhenTheListAlreadyMatches()
        {
            var field = new FakeField();
            var foreign = field.Add("User's own");
            var configs = Configs(
                (TagType.HasAchievements, "Platform", true),
                (TagType.Completed, "Done", true));
            var target = Target(field, configs);
            var game = new Game { TagIds = null };

            Assert.IsTrue(target.SyncGame(game, new[] { TagType.HasAchievements, TagType.Completed }));
            game.TagIds.Add(foreign);
            var before = game.TagIds.ToList();

            Assert.IsFalse(target.SyncGame(game, new[] { TagType.HasAchievements, TagType.Completed }));
            CollectionAssert.AreEqual(before, game.TagIds);

            Assert.IsTrue(target.SyncGame(game, new[] { TagType.HasAchievements }));
            CollectionAssert.AreEqual(new[] { configs[TagType.HasAchievements].TagId.Value, foreign }, game.TagIds);
        }

        [TestMethod]
        public void SyncGame_IgnoresKindsOutsideTheTargetsKinds_EvenWhenConfigured()
        {
            var field = new FakeField();
            var configs = Configs(
                (TagType.HasAchievements, "Platform", true),
                (TagType.Customized, "Customized", true));
            var target = Target(field, configs, TaggingSettings.FeatureKinds);
            var game = new Game();

            target.EnsureConfiguredIds();
            target.SyncGame(game, new[] { TagType.HasAchievements, TagType.Customized });

            Assert.AreEqual(1, field.Items.Count);
            Assert.AreEqual(1, game.TagIds.Count);
            Assert.IsNull(configs[TagType.Customized].TagId);
            Assert.IsFalse(target.Manages(TagType.Customized));
            Assert.IsFalse(target.SetManaged(game, TagType.Customized, true));
        }

        [TestMethod]
        public void SetManaged_AddsAndRemovesOneKind_LeavingTheRestAlone()
        {
            var field = new FakeField();
            var other = field.Add("Other");
            var configs = Configs((TagType.Customized, "Customized", true));
            var target = Target(field, configs);
            var game = new Game { TagIds = new List<Guid> { other } };

            Assert.IsTrue(target.SetManaged(game, TagType.Customized, true));
            Assert.IsFalse(target.SetManaged(game, TagType.Customized, true));
            CollectionAssert.AreEquivalent(new[] { other, configs[TagType.Customized].TagId.Value }, game.TagIds);

            Assert.IsTrue(target.SetManaged(game, TagType.Customized, false));
            CollectionAssert.AreEquivalent(new[] { other }, game.TagIds);
        }

        [TestMethod]
        public void CleanupOrphans_DeletesOnlyIdsAbsentFromTheCurrentSet_AndForgetsThem()
        {
            var field = new FakeField();
            var configs = Configs(
                (TagType.HasAchievements, "Platform", true),
                (TagType.Completed, "Done", false));
            var target = Target(field, configs);
            target.EnsureConfiguredIds();
            var kept = configs[TagType.HasAchievements].TagId.Value;
            var disabled = field.Add("Done");
            configs[TagType.Completed].TagId = disabled;
            var old = new HashSet<Guid> { kept, disabled };

            target.CleanupOrphans(old, target.GetEnabledIds());

            Assert.IsTrue(field.Items.ContainsKey(kept));
            Assert.IsFalse(field.Items.ContainsKey(disabled));
            Assert.AreEqual(kept, configs[TagType.HasAchievements].TagId);
            Assert.IsNull(configs[TagType.Completed].TagId);
        }

        [TestMethod]
        public void DeleteAllTracked_DeletesByIdOnly_AndLeavesASameNamedForeignItem()
        {
            var field = new FakeField();
            var foreign = field.Add("Platform");
            var configs = Configs((TagType.HasAchievements, "Platform", true));
            var target = Target(field, configs);
            configs[TagType.HasAchievements].TagId = field.Add("Platform");
            var tracked = configs[TagType.HasAchievements].TagId.Value;

            var deleted = target.DeleteAllTracked();

            Assert.AreEqual(1, deleted);
            Assert.IsTrue(field.Items.ContainsKey(foreign));
            Assert.IsFalse(field.Items.ContainsKey(tracked));
            Assert.IsNull(configs[TagType.HasAchievements].TagId);
            Assert.AreEqual(0, target.GetAllTrackedIds().Count);
        }

        [TestMethod]
        public void RemoveManaged_WithNoRemovalSet_StripsEveryTrackedId()
        {
            var field = new FakeField();
            var configs = Configs((TagType.HasAchievements, "Platform", true));
            var target = Target(field, configs);
            target.EnsureConfiguredIds();
            var tracked = configs[TagType.HasAchievements].TagId.Value;
            var foreign = field.Add("Mine");
            var game = new Game { TagIds = new List<Guid> { tracked, foreign } };

            Assert.IsTrue(target.RemoveManaged(game));
            CollectionAssert.AreEquivalent(new[] { foreign }, game.TagIds);
            Assert.IsFalse(target.RemoveManaged(new Game { TagIds = null }));
        }

        [TestMethod]
        public void RenameItem_RenamesTheRememberedItem_AndReportsWhetherItChanged()
        {
            var field = new FakeField();
            var configs = Configs((TagType.HasAchievements, "New", true));
            configs[TagType.HasAchievements].TagId = field.Add("Old");
            var target = Target(field, configs);

            Assert.IsTrue(target.RenameItem(configs[TagType.HasAchievements], "Old", "New"));
            Assert.AreEqual("New", field.Items[configs[TagType.HasAchievements].TagId.Value]);
            Assert.IsFalse(target.RenameItem(configs[TagType.HasAchievements], "New", "New"));
            Assert.IsFalse(target.RenameItem(new TagConfig { DisplayName = "x" }, "x", "y"));
        }
    }
}
