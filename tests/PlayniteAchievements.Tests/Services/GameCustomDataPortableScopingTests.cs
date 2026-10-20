using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.Minecraft;
using PlayniteAchievements.Services.GameCustomData;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Tests.Services
{
    [TestClass]
    [TestCategory("Services")]
    public class GameCustomDataPortableScopingTests
    {
        private const string MineStone = "minecraft:story/mine_stone";
        private const string SmeltIron = "minecraft:story/smelt_iron";

        private static readonly IAchievementKeyScope Scope = MinecraftKeyScope.Instance;

        private static AchievementDetail Row(string instance, string advancement, string tab = "Story")
        {
            return new AchievementDetail
            {
                ApiName = instance + "|" + advancement,
                ProviderCategory = instance + "::" + tab,
                Category = instance + "::" + tab
            };
        }

        private static List<AchievementDetail> TwoInstances()
        {
            return new List<AchievementDetail>
            {
                Row("Vanilla", MineStone),
                Row("Vanilla", SmeltIron),
                Row("Modded", MineStone),
                Row("Modded", SmeltIron)
            };
        }

        private static Dictionary<string, T> Map<T>(params (string Key, T Value)[] entries)
        {
            return entries.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
        }

        [TestMethod]
        public void MinecraftKeyScope_SplitsOnTheFirstBar()
        {
            Assert.IsTrue(Scope.TrySplit("Re-Console|minecraft:story/root", out var scope, out var key));
            Assert.AreEqual("Re-Console", scope);
            Assert.AreEqual("minecraft:story/root", key);
            Assert.IsFalse(Scope.TrySplit("minecraft:story/root", out _, out _));
            Assert.IsFalse(Scope.TrySplit("|minecraft:story/root", out _, out _));
        }

        [TestMethod]
        public void AchievementKeyScopes_OnlyMinecraftIsScoped()
        {
            Assert.AreSame(MinecraftKeyScope.Instance, AchievementKeyScopes.For("Minecraft"));
            Assert.IsNull(AchievementKeyScopes.For("Steam"));
            Assert.IsNull(AchievementKeyScopes.For(null));
        }

        [TestMethod]
        public void ToGame_ForeignScope_FansOutToEveryLocalInstance()
        {
            var portable = new GameCustomDataPortableFile
            {
                AchievementUnlockedIconOverrides = Map(("Re-Console|" + MineStone, "images/stone.png")),
                AchievementOverrides = Map(("Re-Console|" + MineStone, new AchievementOverride { UnlockedIconPath = "images/stone.png", Category = "Default" }))
            };

            GameCustomDataPortableScoping.ToGame(portable, Scope, TwoInstances());

            CollectionAssert.AreEquivalent(
                new[] { "Vanilla|" + MineStone, "Modded|" + MineStone },
                portable.AchievementUnlockedIconOverrides.Keys.ToList());
            Assert.AreEqual("images/stone.png", portable.AchievementOverrides["Modded|" + MineStone].UnlockedIconPath);
            Assert.AreEqual("Default", portable.AchievementOverrides["Vanilla|" + MineStone].Category);
            Assert.AreNotSame(
                portable.AchievementOverrides["Vanilla|" + MineStone],
                portable.AchievementOverrides["Modded|" + MineStone]);
        }

        [TestMethod]
        public void ToGame_OwnScopeWinsOverPortableAndForeign()
        {
            var portable = new GameCustomDataPortableFile
            {
                AchievementNotes = Map(
                    ("Other|" + MineStone, "foreign"),
                    (MineStone, "portable"),
                    ("Vanilla|" + MineStone, "own"))
            };

            GameCustomDataPortableScoping.ToGame(portable, Scope, TwoInstances());

            Assert.AreEqual("own", portable.AchievementNotes["Vanilla|" + MineStone]);
            Assert.AreEqual("portable", portable.AchievementNotes["Modded|" + MineStone]);
        }

        [TestMethod]
        public void ToGame_FirstForeignScopeWins()
        {
            var portable = new GameCustomDataPortableFile
            {
                AchievementNotes = Map(("A|" + MineStone, "first"), ("B|" + MineStone, "second"))
            };

            GameCustomDataPortableScoping.ToGame(portable, Scope, TwoInstances());

            Assert.AreEqual("first", portable.AchievementNotes["Vanilla|" + MineStone]);
            Assert.AreEqual("first", portable.AchievementNotes["Modded|" + MineStone]);
        }

        [TestMethod]
        public void ToGame_KeepsKeysThatMatchNothing()
        {
            var portable = new GameCustomDataPortableFile
            {
                AchievementNotes = Map(("Other|minecraft:unknown", "kept"))
            };

            GameCustomDataPortableScoping.ToGame(portable, Scope, TwoInstances());

            Assert.AreEqual("kept", portable.AchievementNotes["Other|minecraft:unknown"]);
        }

        [TestMethod]
        public void ToGame_LaysOrderOutOncePerInstance()
        {
            var portable = new GameCustomDataPortableFile
            {
                AchievementOrder = new List<string> { SmeltIron, MineStone }
            };

            GameCustomDataPortableScoping.ToGame(portable, Scope, TwoInstances());

            CollectionAssert.AreEqual(
                new[] { "Vanilla|" + SmeltIron, "Vanilla|" + MineStone, "Modded|" + SmeltIron, "Modded|" + MineStone },
                portable.AchievementOrder);
        }

        [TestMethod]
        public void ToGame_RootsTokenPathsAtEachInstance()
        {
            var portable = new GameCustomDataPortableFile
            {
                AchievementOverrides = Map((MineStone, new AchievementOverride { Category = "{instance}::Mining" })),
                AchievementCategoryOrder = new List<string> { "{instance}::Mining", "Favorites" },
                AchievementCategoryImageOverrides = Map(("{instance}::Mining", new CategoryImageOverrideData { Art = "images/mining.png" }))
            };

            GameCustomDataPortableScoping.ToGame(portable, Scope, TwoInstances());

            Assert.AreEqual("Vanilla::Mining", portable.AchievementOverrides["Vanilla|" + MineStone].Category);
            Assert.AreEqual("Modded::Mining", portable.AchievementOverrides["Modded|" + MineStone].Category);
            CollectionAssert.AreEqual(new[] { "Vanilla::Mining", "Modded::Mining", "Favorites" }, portable.AchievementCategoryOrder);
            CollectionAssert.AreEquivalent(
                new[] { "Vanilla::Mining", "Modded::Mining" },
                portable.AchievementCategoryImageOverrides.Keys.ToList());
        }

        [TestMethod]
        public void ToGame_RootsForeignScopePathsAtTheLocalInstance()
        {
            var portable = new GameCustomDataPortableFile
            {
                AchievementOverrides = Map(("Re-Console|" + MineStone, new AchievementOverride { Category = "Re-Console::Story" }))
            };

            GameCustomDataPortableScoping.ToGame(portable, Scope, TwoInstances());

            Assert.AreEqual("Vanilla::Story", portable.AchievementOverrides["Vanilla|" + MineStone].Category);
        }

        [TestMethod]
        public void ToGame_WithoutRowsOrScope_LeavesThePackageAlone()
        {
            var portable = new GameCustomDataPortableFile
            {
                AchievementNotes = Map(("Re-Console|" + MineStone, "note"))
            };

            GameCustomDataPortableScoping.ToGame(portable, Scope, null);
            GameCustomDataPortableScoping.ToGame(portable, null, TwoInstances());

            CollectionAssert.AreEqual(new[] { "Re-Console|" + MineStone }, portable.AchievementNotes.Keys.ToList());
        }

        [TestMethod]
        public void ToPortable_CollapsesValuesEveryInstanceShares()
        {
            var portable = new GameCustomDataPortableFile
            {
                AchievementOverrides = Map(
                    ("Vanilla|" + MineStone, new AchievementOverride { UnlockedIconPath = @"C:\a\stone.png", Category = "Vanilla::Mining" }),
                    ("Modded|" + MineStone, new AchievementOverride { UnlockedIconPath = @"C:\b\stone.png", Category = "Modded::Mining" })),
                FilteredAchievementApiNames = new List<string> { "Vanilla|" + SmeltIron, "Modded|" + SmeltIron }
            };

            GameCustomDataPortableScoping.ToPortable(portable, Scope, TwoInstances(), path => System.IO.Path.GetFileName(path));

            CollectionAssert.AreEqual(new[] { MineStone }, portable.AchievementOverrides.Keys.ToList());
            Assert.AreEqual("{instance}::Mining", portable.AchievementOverrides[MineStone].Category);
            Assert.AreEqual(@"C:\a\stone.png", portable.AchievementUnlockedIconOverrides[MineStone]);
            CollectionAssert.AreEqual(new[] { SmeltIron }, portable.FilteredAchievementApiNames);
        }

        [TestMethod]
        public void CategoryKinds_CollapseWhenEveryInstanceSharesAndFanOutOnImport()
        {
            var portable = new GameCustomDataPortableFile
            {
                AchievementCategoryKinds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Vanilla::Mining"] = "DLC",
                    ["Modded::Mining"] = "DLC",
                    ["Vanilla::Story"] = "Base",
                    ["Modded::Story"] = "Subset"
                }
            };

            GameCustomDataPortableScoping.ToPortable(portable, Scope, TwoInstances());

            Assert.AreEqual("DLC", portable.AchievementCategoryKinds["{instance}::Mining"]);
            Assert.AreEqual("Base", portable.AchievementCategoryKinds["Vanilla::Story"], "differing kinds stay scoped");
            Assert.AreEqual("Subset", portable.AchievementCategoryKinds["Modded::Story"]);

            GameCustomDataPortableScoping.ToGame(portable, Scope, TwoInstances());

            Assert.AreEqual("DLC", portable.AchievementCategoryKinds["Vanilla::Mining"]);
            Assert.AreEqual("DLC", portable.AchievementCategoryKinds["Modded::Mining"]);
        }

        [TestMethod]
        public void ToPortable_KeepsScopedValuesThatDiffer()
        {
            var portable = new GameCustomDataPortableFile
            {
                AchievementOverrides = Map(
                    ("Vanilla|" + MineStone, new AchievementOverride { Note = "one" }),
                    ("Modded|" + MineStone, new AchievementOverride { Note = "two" }))
            };

            GameCustomDataPortableScoping.ToPortable(portable, Scope, TwoInstances());

            CollectionAssert.AreEquivalent(
                new[] { "Vanilla|" + MineStone, "Modded|" + MineStone },
                portable.AchievementOverrides.Keys.ToList());
            Assert.AreEqual("two", portable.AchievementNotes["Modded|" + MineStone]);
        }

        [TestMethod]
        public void ToPortable_KeepsAnEntryOnlyOneInstanceHas()
        {
            var portable = new GameCustomDataPortableFile
            {
                FilteredAchievementApiNames = new List<string> { "Vanilla|" + SmeltIron }
            };

            GameCustomDataPortableScoping.ToPortable(portable, Scope, TwoInstances());

            CollectionAssert.AreEqual(new[] { "Vanilla|" + SmeltIron }, portable.FilteredAchievementApiNames);
        }

        [TestMethod]
        public void ToPortable_SingleInstanceGame_IsFullyPortable()
        {
            var rows = new List<AchievementDetail> { Row("Re-Console", MineStone), Row("Re-Console", SmeltIron) };
            var portable = new GameCustomDataPortableFile
            {
                AchievementOrder = new List<string> { "Re-Console|" + SmeltIron, "Re-Console|" + MineStone },
                Capstones = new List<CapstoneAssignment> { new CapstoneAssignment { ApiName = "Re-Console|" + SmeltIron } },
                AchievementCategoryOrder = new List<string> { "Re-Console::Story" }
            };

            GameCustomDataPortableScoping.ToPortable(portable, Scope, rows);

            CollectionAssert.AreEqual(new[] { SmeltIron, MineStone }, portable.AchievementOrder);
            Assert.AreEqual(SmeltIron, portable.Capstones.Single().ApiName);
            CollectionAssert.AreEqual(new[] { "{instance}::Story" }, portable.AchievementCategoryOrder);
        }

        [TestMethod]
        public void ExportThenImport_LandsOnAnotherMachinesInstances()
        {
            var source = new List<AchievementDetail> { Row("Re-Console", MineStone) };
            var portable = new GameCustomDataPortableFile
            {
                AchievementOverrides = Map(("Re-Console|" + MineStone, new AchievementOverride { UnlockedIconPath = "stone.png", Category = "Re-Console::Mining" }))
            };

            GameCustomDataPortableScoping.ToPortable(portable, Scope, source);
            GameCustomDataPortableScoping.ToGame(portable, Scope, TwoInstances());

            Assert.AreEqual("Vanilla::Mining", portable.AchievementOverrides["Vanilla|" + MineStone].Category);
            Assert.AreEqual("Modded::Mining", portable.AchievementOverrides["Modded|" + MineStone].Category);
            Assert.AreEqual("stone.png", portable.AchievementUnlockedIconOverrides["Modded|" + MineStone]);
        }
    }
}
