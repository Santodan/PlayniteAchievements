using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Hydration;
using PlayniteAchievements.Services.Images;
using PlayniteAchievements.Services.Workshop.Preview;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;

namespace PlayniteAchievements.Services.Tests
{
    [TestClass]
    [DoNotParallelize]
    public class GameCustomDataPreviewDiffBuilderTests
    {
        private static readonly Guid GameId = Guid.Parse("3c0f1e2d-0000-4000-8000-000000000001");

        private string _tempDir;

        [TestInitialize]
        public void Initialize()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "PlayniteAchievementsTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        [TestCleanup]
        public void Cleanup()
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        [TestMethod]
        public void NameAndDescriptionOverride_FlagsBothAndCountsTheRest()
        {
            var manifest = new GameCustomDataPortableFile
            {
                AchievementOverrides = Overrides(("a1", new AchievementOverride { DisplayName = "Renamed", Description = "New text" }))
            };

            var diff = GameCustomDataPreviewDiffBuilder.Build(ManifestPackage(manifest), Source(current: null));

            Assert.IsFalse(diff.IsPackageOnly);
            Assert.AreEqual("Game", diff.ComparedAgainstGameName);
            var row = diff.Rows.Single();
            Assert.AreEqual("a1", row.ApiName);
            Assert.AreEqual(AchievementPreviewChange.Name | AchievementPreviewChange.Description, row.Changes);
            Assert.AreEqual("A1", row.Before.DisplayName);
            Assert.AreEqual("Renamed", row.After.DisplayName);
            Assert.AreEqual("New text", row.After.Description);
            Assert.AreEqual(2, diff.UnchangedCount);
            CollectionAssert.AreEqual(new[] { "a2", "a3" }, diff.UnchangedRows.Select(unchanged => unchanged.ApiName).ToList());
            Assert.IsTrue(diff.UnchangedRows.All(unchanged =>
                unchanged.Changes == AchievementPreviewChange.None && unchanged.Before != null && unchanged.After != null));
            Assert.IsFalse(diff.OrderChanged);
            Assert.IsFalse(diff.HasBaseline);
        }

        [TestMethod]
        public void CategoryAndCapstone_AreFlaggedOnTheirRows()
        {
            var manifest = new GameCustomDataPortableFile
            {
                AchievementOverrides = Overrides(("a2", new AchievementOverride { Category = "DLC" })),
                CapstonesMaterialized = true,
                Capstones = new List<CapstoneAssignment> { new CapstoneAssignment { ApiName = "a3" } }
            };

            var diff = GameCustomDataPreviewDiffBuilder.Build(ManifestPackage(manifest), Source(current: null));

            var category = diff.Rows.Single(row => row.ApiName == "a2");
            Assert.AreEqual(AchievementPreviewChange.Category, category.Changes);
            Assert.AreEqual("DLC", category.After.Category);
            var capstone = diff.Rows.Single(row => row.ApiName == "a3");
            Assert.AreEqual(AchievementPreviewChange.Capstone, capstone.Changes);
            Assert.IsFalse(capstone.Before.IsCapstone);
            Assert.IsTrue(capstone.After.IsCapstone);
            Assert.AreEqual(1, diff.UnchangedCount);
            Assert.AreEqual(1, diff.CapstoneCount);
            Assert.AreEqual(1, diff.CategoryCount);
        }

        [TestMethod]
        public void CustomAchievement_IsAdded_AndOneThePackageDropsIsRemovedLast()
        {
            var current = new GameCustomDataFile
            {
                PlayniteGameId = GameId,
                CustomAchievements = new List<CustomAchievementDefinition>
                {
                    new CustomAchievementDefinition { Id = "old", DisplayName = "Old one" }
                }
            };
            var manifest = new GameCustomDataPortableFile
            {
                CustomAchievements = new List<CustomAchievementDefinition>
                {
                    new CustomAchievementDefinition { Id = "bonus", DisplayName = "Bonus" }
                }
            };

            var diff = GameCustomDataPreviewDiffBuilder.Build(ManifestPackage(manifest), Source(current));

            Assert.AreEqual(2, diff.Rows.Count);
            var added = diff.Rows[0];
            Assert.AreEqual(CustomAchievementProjectionService.BuildApiName("bonus"), added.ApiName);
            Assert.AreEqual(AchievementPreviewChange.Added, added.Changes);
            Assert.IsNull(added.Before);
            Assert.IsTrue(added.After.IsCustom);
            Assert.AreEqual("Bonus", added.After.DisplayName);
            var removed = diff.Rows[1];
            Assert.AreEqual(CustomAchievementProjectionService.BuildApiName("old"), removed.ApiName);
            Assert.AreEqual(AchievementPreviewChange.Removed, removed.Changes);
            Assert.IsNull(removed.After);
            Assert.AreEqual(1, diff.CustomAchievementCount);
        }

        [TestMethod]
        public void OrderChanged_ComparesThePredictedOrderWithTheCurrentOne()
        {
            var reordered = new GameCustomDataPortableFile { AchievementOrder = new List<string> { "a3", "a1", "a2" } };
            var diff = GameCustomDataPreviewDiffBuilder.Build(ManifestPackage(reordered), Source(current: null));
            Assert.IsTrue(diff.OrderChanged);
            Assert.AreEqual(0, diff.Rows.Count, "order alone changes no row");
            Assert.AreEqual(3, diff.UnchangedCount);
            CollectionAssert.AreEqual(
                new[] { "a3", "a1", "a2" },
                diff.UnchangedRows.Select(row => row.ApiName).ToList(),
                "unchanged rows follow the order after the install");

            var current = new GameCustomDataFile { PlayniteGameId = GameId, AchievementOrder = new List<string> { "a3", "a1", "a2" } };
            var same = GameCustomDataPreviewDiffBuilder.Build(ManifestPackage(reordered), Source(current));
            Assert.IsFalse(same.OrderChanged);
        }

        [TestMethod]
        public void Rows_FollowTheOrderTheGameShowsAfterTheInstall()
        {
            var manifest = new GameCustomDataPortableFile
            {
                AchievementOrder = new List<string> { "a3", "a2", "a1" },
                AchievementOverrides = Overrides(
                    ("a1", new AchievementOverride { DisplayName = "One" }),
                    ("a3", new AchievementOverride { DisplayName = "Three" }))
            };

            var diff = GameCustomDataPreviewDiffBuilder.Build(ManifestPackage(manifest), Source(current: null));

            CollectionAssert.AreEqual(new[] { "a3", "a1" }, diff.Rows.Select(row => row.ApiName).ToArray());
        }

        [TestMethod]
        public void BaselineEdit_IsKept_AndTheRowShowsTheUsersValue()
        {
            var baseline = new GameCustomDataFile
            {
                PlayniteGameId = GameId,
                AchievementOverrides = Overrides(("a1", new AchievementOverride { DisplayName = "Pack v1" }))
            };
            var current = new GameCustomDataFile
            {
                PlayniteGameId = GameId,
                AchievementOverrides = Overrides(("a1", new AchievementOverride { DisplayName = "Mine" }))
            };
            var manifest = new GameCustomDataPortableFile
            {
                AchievementOverrides = Overrides(("a1", new AchievementOverride { DisplayName = "Pack v2" })),
                FilteredAchievementApiNames = new List<string> { "a1" }
            };

            var diff = GameCustomDataPreviewDiffBuilder.Build(ManifestPackage(manifest), Source(current, baseline));

            Assert.IsTrue(diff.HasBaseline);
            Assert.IsTrue(diff.KeptEditCount > 0);
            var row = diff.Rows.Single();
            Assert.AreEqual("a1", row.ApiName);
            Assert.AreEqual(AchievementPreviewChange.Filter, row.Changes, "the kept name is no change; the untouched filter list takes the package's");
            Assert.AreEqual("Mine", row.Before.DisplayName);
            Assert.AreEqual("Mine", row.After.DisplayName);
            Assert.IsTrue(row.After.IsFiltered);
        }

        [TestMethod]
        public void WithoutBaseline_ThePackageValueReplacesTheUsersEdit()
        {
            var current = new GameCustomDataFile
            {
                PlayniteGameId = GameId,
                AchievementOverrides = Overrides(("a1", new AchievementOverride { DisplayName = "Mine" }))
            };
            var manifest = new GameCustomDataPortableFile
            {
                AchievementOverrides = Overrides(("a1", new AchievementOverride { DisplayName = "Pack v2" }))
            };

            var diff = GameCustomDataPreviewDiffBuilder.Build(ManifestPackage(manifest), Source(current));

            Assert.IsFalse(diff.HasBaseline);
            Assert.AreEqual(0, diff.KeptEditCount);
            Assert.AreEqual("Pack v2", diff.Rows.Single().After.DisplayName);
        }

        [TestMethod]
        public void Icons_WithTheSameContentAreNoChange()
        {
            var currentIcon = WriteFile("current.png", 1, 2, 3);
            var sameIcon = WriteFile("same.png", 1, 2, 3);
            var otherIcon = WriteFile("other.png", 9, 9, 9);
            var current = new GameCustomDataFile
            {
                PlayniteGameId = GameId,
                AchievementOverrides = Overrides(
                    ("a1", new AchievementOverride { UnlockedIconPath = currentIcon }),
                    ("a2", new AchievementOverride { UnlockedIconPath = currentIcon }))
            };
            var manifest = new GameCustomDataPortableFile
            {
                AchievementOverrides = Overrides(
                    ("a1", new AchievementOverride { UnlockedIconPath = sameIcon, Description = "changed" }),
                    ("a2", new AchievementOverride { UnlockedIconPath = otherIcon }))
            };

            var diff = GameCustomDataPreviewDiffBuilder.Build(ManifestPackage(manifest), Source(current));

            Assert.AreEqual(AchievementPreviewChange.Description, diff.Rows.Single(row => row.ApiName == "a1").Changes);
            var a2 = diff.Rows.Single(row => row.ApiName == "a2");
            Assert.AreEqual(AchievementPreviewChange.UnlockedIcon, a2.Changes);
            Assert.AreEqual(otherIcon, a2.After.UnlockedIcon);
        }

        [TestMethod]
        public void CustomAchievementsCsv_MergesIntoTheCurrentRecord()
        {
            var current = new GameCustomDataFile
            {
                PlayniteGameId = GameId,
                AchievementOverrides = Overrides(("a1", new AchievementOverride { DisplayName = "Mine" })),
                CustomAchievements = new List<CustomAchievementDefinition>
                {
                    new CustomAchievementDefinition { Id = "kept", DisplayName = "Kept" }
                }
            };
            var parsed = new CustomAchievementTextImportResult();
            parsed.Definitions.Add(new CustomAchievementDefinition { Id = "fresh", DisplayName = "Fresh" });
            var package = new GameCustomDataPortablePackage(GameCustomDataPackageShape.CustomAchievementsCsv, null, parsed, null, null);

            var diff = GameCustomDataPreviewDiffBuilder.Build(package, Source(current));

            var row = diff.Rows.Single();
            Assert.AreEqual(CustomAchievementProjectionService.BuildApiName("fresh"), row.ApiName);
            Assert.AreEqual(AchievementPreviewChange.Added, row.Changes);
            Assert.AreEqual(4, diff.UnchangedCount, "the rename and the existing custom achievement stay");
        }

        [TestMethod]
        public void ImageOnly_WithSource_SetsTheMatchingIconsOnly()
        {
            var icon = WriteFile("a1.png", 4, 5, 6);
            var package = new GameCustomDataPortablePackage(
                GameCustomDataPackageShape.ImageOnly,
                null,
                null,
                null,
                new[]
                {
                    new GameCustomDataImageOnlyEntry("a1", AchievementIconVariant.Unlocked, icon),
                    new GameCustomDataImageOnlyEntry("not_in_game", AchievementIconVariant.Unlocked, icon)
                });

            var diff = GameCustomDataPreviewDiffBuilder.Build(package, Source(current: null));

            var row = diff.Rows.Single();
            Assert.AreEqual("a1", row.ApiName);
            Assert.AreEqual(AchievementPreviewChange.UnlockedIcon, row.Changes);
            Assert.AreEqual(icon, row.After.UnlockedIcon);
        }

        [TestMethod]
        public void PackageOnly_ListsWhatThePackageSetsWithoutBefore()
        {
            var unlocked = WriteFile("u.png", 1);
            var locked = WriteFile("l.png", 2);
            var manifest = new GameCustomDataPortableFile
            {
                AchievementOverrides = Overrides(
                    ("a1", new AchievementOverride { DisplayName = "Renamed", Note = "note", UnlockedIconPath = unlocked, LockedIconPath = locked }),
                    ("a2", new AchievementOverride { Category = "Story" })),
                CapstonesMaterialized = true,
                Capstones = new List<CapstoneAssignment> { new CapstoneAssignment { ApiName = "a3" } },
                CustomAchievements = new List<CustomAchievementDefinition>
                {
                    new CustomAchievementDefinition { Id = "bonus", DisplayName = "Bonus", Category = "Extras" }
                },
                NotificationAppearanceOverride = new GameNotificationAppearanceOverride { Style = NotificationStyleSettings.CreateDefault() }
            };

            var diff = GameCustomDataPreviewDiffBuilder.Build(ManifestPackage(manifest), source: null);

            Assert.IsTrue(diff.IsPackageOnly);
            Assert.IsNull(diff.ComparedAgainstGameName);
            Assert.AreEqual(4, diff.Rows.Count);
            Assert.AreEqual(0, diff.UnchangedRows.Count);
            Assert.IsTrue(diff.Rows.All(row => row.Before == null && row.After != null));

            var a1 = diff.Rows.Single(row => row.ApiName == "a1");
            Assert.AreEqual(
                AchievementPreviewChange.Name | AchievementPreviewChange.Note | AchievementPreviewChange.UnlockedIcon | AchievementPreviewChange.LockedIcon,
                a1.Changes);
            Assert.AreEqual(unlocked, a1.After.UnlockedIcon);
            Assert.AreEqual(locked, a1.After.LockedIcon);
            Assert.AreEqual(AchievementPreviewChange.Capstone, diff.Rows.Single(row => row.ApiName == "a3").Changes);
            var custom = diff.Rows.Single(row => row.After.IsCustom);
            Assert.IsTrue(custom.Changes.HasFlag(AchievementPreviewChange.Added));

            Assert.AreEqual(2, diff.IconCount);
            Assert.AreEqual(2, diff.OverrideCount);
            Assert.AreEqual(2, diff.CategoryCount);
            Assert.AreEqual(1, diff.CapstoneCount);
            Assert.AreEqual(1, diff.CustomAchievementCount);
            Assert.AreEqual(1, diff.NotesCount);
            Assert.IsTrue(diff.HasNotificationStyle);
        }

        [TestMethod]
        public void PackageOnly_ImageOnly_GroupsEachAchievementsIcons()
        {
            var package = new GameCustomDataPortablePackage(
                GameCustomDataPackageShape.ImageOnly,
                null,
                null,
                null,
                new[]
                {
                    new GameCustomDataImageOnlyEntry("a1", AchievementIconVariant.Unlocked, @"C:\scratch\a1.png"),
                    new GameCustomDataImageOnlyEntry("a1", AchievementIconVariant.Locked, @"C:\scratch\a1.locked.png"),
                    new GameCustomDataImageOnlyEntry("a2", AchievementIconVariant.Unlocked, @"C:\scratch\a2.png")
                });

            var diff = GameCustomDataPreviewDiffBuilder.Build(package, source: null);

            Assert.IsTrue(diff.IsPackageOnly);
            Assert.AreEqual(2, diff.Rows.Count);
            var a1 = diff.Rows[0];
            Assert.AreEqual(AchievementPreviewChange.UnlockedIcon | AchievementPreviewChange.LockedIcon, a1.Changes);
            Assert.AreEqual(@"C:\scratch\a1.locked.png", a1.After.LockedIcon);
            Assert.IsNull(a1.After.DisplayName);
            Assert.AreEqual(3, diff.IconCount);
        }

        [TestMethod]
        public void SourceRawData_IsNotModified()
        {
            var source = Source(current: null);
            var manifest = new GameCustomDataPortableFile
            {
                AchievementOverrides = Overrides(("a1", new AchievementOverride { DisplayName = "Renamed" }))
            };

            GameCustomDataPreviewDiffBuilder.Build(ManifestPackage(manifest), source);

            Assert.AreEqual("A1", source.RawData.Achievements[0].DisplayName);
            Assert.AreEqual(3, source.RawData.Achievements.Count);
        }

        [TestMethod]
        public void AfterData_IsTheHydratedGameAfterTheInstall_AndBeforeDataTheCurrentOne()
        {
            var source = Source(current: null);
            var manifest = new GameCustomDataPortableFile
            {
                AchievementOrder = new List<string> { "a3", "a1", "a2" },
                AchievementCategoryOrder = new List<string> { "DLC" },
                AchievementOverrides = Overrides(("a1", new AchievementOverride { DisplayName = "Renamed", Category = "DLC" })),
                CapstonesMaterialized = true,
                Capstones = new List<CapstoneAssignment> { new CapstoneAssignment { ApiName = "a2" } },
                CustomAchievements = new List<CustomAchievementDefinition>
                {
                    new CustomAchievementDefinition { Id = "bonus", DisplayName = "Bonus", TrophyType = "gold", Points = 50 }
                }
            };

            var diff = GameCustomDataPreviewDiffBuilder.Build(ManifestPackage(manifest), source);

            Assert.AreSame(source.CurrentData, diff.BeforeData);
            var after = diff.AfterData;
            Assert.IsNotNull(after);
            Assert.AreNotSame(source.RawData, after);
            Assert.AreEqual(GameId, after.PlayniteGameId);
            CollectionAssert.AreEqual(new[] { "a3", "a1", "a2" }, after.AchievementOrder);
            CollectionAssert.AreEqual(new[] { "DLC" }, after.AchievementCategoryOrder);
            Assert.AreEqual(4, after.Achievements.Count);

            var a1 = after.Achievements.Single(achievement => achievement.ApiName == "a1");
            Assert.AreEqual("Renamed", a1.DisplayName);
            Assert.AreEqual("DLC", a1.Category);
            Assert.IsTrue(after.Achievements.Single(achievement => achievement.ApiName == "a2").IsCapstone);
            var bonus = after.Achievements.Single(achievement => achievement.IsCustom);
            Assert.AreEqual("gold", bonus.TrophyType);
            Assert.AreEqual(50, bonus.Points);
            Assert.AreEqual("A1", diff.BeforeData.Achievements.Single(achievement => achievement.ApiName == "a1").DisplayName);
        }

        [TestMethod]
        public void PackageOnly_HasNoAfterOrBeforeData()
        {
            var manifest = new GameCustomDataPortableFile
            {
                AchievementOverrides = Overrides(("a1", new AchievementOverride { DisplayName = "Renamed" }))
            };

            var diff = GameCustomDataPreviewDiffBuilder.Build(ManifestPackage(manifest), source: null);

            Assert.IsNull(diff.AfterData);
            Assert.IsNull(diff.BeforeData);
        }

        [TestMethod]
        public void SharedFromItsOwnGame_ChangesNothing_ButListsWhatThePackageTouches()
        {
            GameCustomDataFile Record() => new GameCustomDataFile
            {
                PlayniteGameId = GameId,
                AchievementOverrides = Overrides(("a1", new AchievementOverride { DisplayName = "Renamed", Category = "DLC" })),
                CapstonesMaterialized = true,
                Capstones = new List<CapstoneAssignment> { new CapstoneAssignment { ApiName = "a3" } }
            };
            var manifest = new GameCustomDataPortableFile
            {
                AchievementOverrides = Record().AchievementOverrides,
                CapstonesMaterialized = true,
                Capstones = Record().Capstones
            };

            var diff = GameCustomDataPreviewDiffBuilder.Build(ManifestPackage(manifest), Source(Record()));
            var packageOnly = GameCustomDataPreviewDiffBuilder.Build(ManifestPackage(manifest), source: null);

            Assert.AreEqual(0, diff.Rows.Count);
            CollectionAssert.AreEquivalent(new[] { "a1", "a3" }, diff.PackageTouchedApiNames.ToList());
            CollectionAssert.AreEquivalent(new[] { "a1", "a3" }, packageOnly.PackageTouchedApiNames.ToList());
            var after = diff.AfterData.Achievements;
            Assert.AreEqual("Renamed", after.Single(achievement => achievement.ApiName == "a1").DisplayName);
            Assert.IsTrue(after.Single(achievement => achievement.ApiName == "a3").IsCapstone);
        }

        [TestMethod]
        public void PackageTouchedApiNames_IncludeCustomAchievementsAndIcons()
        {
            var manifest = new GameCustomDataPortableFile
            {
                AchievementUnlockedIconOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["a2"] = WriteFile("a2.png", 1, 2, 3)
                },
                CustomAchievements = new List<CustomAchievementDefinition>
                {
                    new CustomAchievementDefinition { Id = "bonus", DisplayName = "Bonus" }
                }
            };

            var diff = GameCustomDataPreviewDiffBuilder.Build(ManifestPackage(manifest), Source(current: null));

            CollectionAssert.AreEquivalent(
                new[] { "a2", CustomAchievementProjectionService.BuildApiName("bonus") },
                diff.PackageTouchedApiNames.ToList());
        }

        [TestMethod]
        public void HidePersonalProgress_BuildsEveryAfterRowUnlockedWithNoTimeOrProgress()
        {
            var source = Source(current: null);
            var unlockedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            foreach (var data in new[] { source.RawData, source.CurrentData })
            {
                data.Achievements[0].UnlockTimeUtc = unlockedAt;
                data.Achievements[1].ProgressNum = 3;
                data.Achievements[1].ProgressDenom = 10;
            }

            source.HidePersonalProgress = true;
            var manifest = new GameCustomDataPortableFile
            {
                AchievementOverrides = Overrides(("a1", new AchievementOverride { DisplayName = "Renamed" })),
                CustomAchievements = new List<CustomAchievementDefinition>
                {
                    new CustomAchievementDefinition { Id = "bonus", DisplayName = "Bonus" }
                }
            };

            var diff = GameCustomDataPreviewDiffBuilder.Build(ManifestPackage(manifest), source);

            Assert.AreEqual(4, diff.AfterData.Achievements.Count);
            Assert.IsTrue(diff.AfterData.Achievements.All(achievement =>
                achievement.Unlocked &&
                achievement.UnlockTimeUtc == null &&
                achievement.ProgressNum == null &&
                achievement.ProgressDenom == null));
            Assert.AreEqual(unlockedAt, source.RawData.Achievements[0].UnlockTimeUtc);
            Assert.IsFalse(source.RawData.Achievements[2].Unlocked);
            Assert.AreEqual(unlockedAt, diff.BeforeData.Achievements[0].UnlockTimeUtc);
        }

        [TestMethod]
        public void WithoutHidePersonalProgress_AfterRowsKeepTheUnlockState()
        {
            var source = Source(current: null);
            var unlockedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            source.RawData.Achievements[0].UnlockTimeUtc = unlockedAt;

            var diff = GameCustomDataPreviewDiffBuilder.Build(ManifestPackage(new GameCustomDataPortableFile
            {
                AchievementOverrides = Overrides(("a1", new AchievementOverride { DisplayName = "Renamed" }))
            }), source);

            var after = diff.AfterData.Achievements;
            Assert.AreEqual(unlockedAt, after.Single(achievement => achievement.ApiName == "a1").UnlockTimeUtc);
            Assert.IsFalse(after.Single(achievement => achievement.ApiName == "a2").Unlocked);
        }

        [TestMethod]
        public void PreviewState_RaisesPropertyChangedWhenAnIconIsSwapped()
        {
            var state = new AchievementPreviewState { UnlockedIcon = "path" };
            var raised = new List<string>();
            ((INotifyPropertyChanged)state).PropertyChanged += (sender, args) => raised.Add(args.PropertyName);

            state.UnlockedIcon = new object();
            state.LockedIcon = "locked";

            CollectionAssert.AreEqual(new[] { nameof(AchievementPreviewState.UnlockedIcon), nameof(AchievementPreviewState.LockedIcon) }, raised);
        }

        [TestMethod]
        public void ScopedPackage_ChangesEveryInstance_ButCountsWhatItCarries()
        {
            var icon = WriteFile("stone.png", 1, 2, 3);
            var manifest = new GameCustomDataPortableFile
            {
                AchievementOverrides = Overrides(("Re-Console|minecraft:story/mine_stone", new AchievementOverride { UnlockedIconPath = icon }))
            };
            GameAchievementData Rows() => new GameAchievementData
            {
                PlayniteGameId = GameId,
                GameName = "Minecraft",
                ProviderKey = "Minecraft",
                Achievements = new List<AchievementDetail>
                {
                    new AchievementDetail { ApiName = "Vanilla|minecraft:story/mine_stone", DisplayName = "Stone Age", Category = "Vanilla::Minecraft" },
                    new AchievementDetail { ApiName = "Modded|minecraft:story/mine_stone", DisplayName = "Stone Age", Category = "Modded::Minecraft" }
                }
            };
            var source = new GameCustomDataPreviewSource
            {
                GameId = GameId,
                GameName = "Minecraft",
                RawData = Rows(),
                CurrentData = Rows(),
                Persisted = new PersistedSettings()
            };

            var diff = GameCustomDataPreviewDiffBuilder.Build(ManifestPackage(manifest), source);

            Assert.AreEqual(1, diff.IconCount);
            CollectionAssert.AreEquivalent(
                new[] { "Vanilla|minecraft:story/mine_stone", "Modded|minecraft:story/mine_stone" },
                diff.Rows.Select(row => row.ApiName).ToList());
            Assert.IsTrue(diff.Rows.All(row => row.Changes.HasFlag(AchievementPreviewChange.UnlockedIcon)));
            CollectionAssert.AreEquivalent(
                new[] { "Vanilla|minecraft:story/mine_stone", "Modded|minecraft:story/mine_stone" },
                diff.PackageTouchedApiNames.ToList());
        }

        private static GameCustomDataPortablePackage ManifestPackage(GameCustomDataPortableFile manifest)
        {
            // The shape ReadPortablePackage hands out: normalized, personal state stripped.
            var normalized = GameCustomDataNormalizer.NormalizePortable(manifest, Guid.Empty);
            PortablePersonalState.Strip(normalized);
            return new GameCustomDataPortablePackage(GameCustomDataPackageShape.Manifest, normalized, null, null, null);
        }

        private static GameAchievementData Raw()
        {
            return new GameAchievementData
            {
                PlayniteGameId = GameId,
                GameName = "Game",
                ProviderKey = "Steam",
                Achievements = new List<AchievementDetail>
                {
                    new AchievementDetail { ApiName = "a1", DisplayName = "A1", Description = "first" },
                    new AchievementDetail { ApiName = "a2", DisplayName = "A2", Description = "second" },
                    new AchievementDetail { ApiName = "a3", DisplayName = "A3", Description = "third" }
                }
            };
        }

        /// <summary>
        /// A comparison source whose current rows are the raw rows hydrated with
        /// <paramref name="current"/>, as the live hydration produces them.
        /// </summary>
        private static GameCustomDataPreviewSource Source(GameCustomDataFile current, GameCustomDataFile baseline = null)
        {
            var persisted = new PersistedSettings();
            var stored = current == null ? null : GameCustomDataNormalizer.NormalizeInternal(current, GameId);
            var record = stored ?? new GameCustomDataFile { PlayniteGameId = GameId };
            var currentData = Raw();
            AchievementOverlayPipeline.Apply(
                currentData,
                GameId,
                GameCustomDataLookup.BuildResolvedFromRecord(record, persisted),
                managedCustomIconService: null,
                () => AchievementOverride.Project(record.AchievementOverrides, o => o.UnlockedIconPath),
                () => AchievementOverride.Project(record.AchievementOverrides, o => o.LockedIconPath));

            return new GameCustomDataPreviewSource
            {
                GameId = GameId,
                GameName = "Game",
                RawData = Raw(),
                CurrentData = currentData,
                Current = stored,
                Baseline = baseline == null ? null : GameCustomDataNormalizer.NormalizeInternal(baseline, GameId),
                Persisted = persisted
            };
        }

        private static Dictionary<string, AchievementOverride> Overrides(params (string ApiName, AchievementOverride Value)[] entries)
        {
            return entries.ToDictionary(entry => entry.ApiName, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
        }

        private string WriteFile(string name, params byte[] bytes)
        {
            var path = Path.Combine(_tempDir, name);
            File.WriteAllBytes(path, bytes);
            return path;
        }
    }
}
