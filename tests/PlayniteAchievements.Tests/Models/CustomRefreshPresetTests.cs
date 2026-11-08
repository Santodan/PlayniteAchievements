using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Achievements;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Models.Tests
{
    [TestClass]
    public class CustomRefreshPresetTests
    {
        [TestMethod]
        public void NormalizePresets_RemovesInvalidNames_DedupesAndKeepsEveryPreset()
        {
            var presets = new List<CustomRefreshPreset>
            {
                null,
                new CustomRefreshPreset { Name = "  ", Options = new CustomRefreshOptions() },
                new CustomRefreshPreset { Name = "  Alpha  ", Options = new CustomRefreshOptions() },
                new CustomRefreshPreset { Name = "alpha", Options = new CustomRefreshOptions() }
            };

            for (var i = 0; i < 60; i++)
            {
                presets.Add(new CustomRefreshPreset
                {
                    Name = $"Preset {i}",
                    Options = new CustomRefreshOptions()
                });
            }

            var normalized = CustomRefreshPreset.NormalizePresets(presets);

            // No limit: every named, distinct preset is kept ("Alpha" once plus the 60 numbered).
            Assert.AreEqual(61, normalized.Count);
            Assert.IsTrue(normalized.All(preset => !string.IsNullOrWhiteSpace(preset.Name)));
            Assert.AreEqual(
                normalized.Count,
                normalized.Select(preset => preset.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.IsTrue(normalized.Any(preset => string.Equals(preset.Name, "Alpha", StringComparison.OrdinalIgnoreCase)));
        }

        [TestMethod]
        public void CustomRefreshPresetClone_DeepCopiesOptions()
        {
            var gameId = Guid.NewGuid();
            var preset = new CustomRefreshPreset
            {
                Name = "  Steam Recent  ",
                Options = new CustomRefreshOptions
                {
                    ProviderKeys = new List<string> { "Steam" },
                    Scope = CustomGameScope.Recent,
                    IncludeGameIds = new List<Guid> { gameId },
                    ExcludeGameIds = new List<Guid>(),
                    RunProvidersInParallelOverride = false
                }
            };

            var clone = preset.Clone();

            Assert.AreEqual("Steam Recent", clone.Name);
            Assert.IsNotNull(clone.Options);
            Assert.AreNotSame(preset.Options, clone.Options);
            CollectionAssert.AreEqual(new[] { "Steam" }, clone.Options.ProviderKeys.ToList());
            CollectionAssert.AreEqual(new[] { gameId }, clone.Options.IncludeGameIds.ToList());

            var cloneProviders = clone.Options.ProviderKeys as List<string>;
            cloneProviders.Add("Epic");
            Assert.AreEqual(1, preset.Options.ProviderKeys.Count);
            Assert.AreEqual(2, clone.Options.ProviderKeys.Count);
        }

        [TestMethod]
        public void PersistedSettingsClone_DeepCopiesCustomRefreshPresets()
        {
            var gameId = Guid.NewGuid();
            var settings = new PersistedSettings
            {
                CustomRefreshPresets = new List<CustomRefreshPreset>
                {
                    new CustomRefreshPreset
                    {
                        Name = "Preset One",
                        Options = new CustomRefreshOptions
                        {
                            ProviderKeys = new List<string> { "Steam" },
                            IncludeGameIds = new List<Guid> { gameId }
                        }
                    }
                }
            };

            var clone = settings.Clone();

            Assert.IsNotNull(clone.CustomRefreshPresets);
            Assert.AreEqual(1, clone.CustomRefreshPresets.Count);
            Assert.AreNotSame(settings.CustomRefreshPresets, clone.CustomRefreshPresets);
            Assert.AreNotSame(settings.CustomRefreshPresets[0], clone.CustomRefreshPresets[0]);
            Assert.AreNotSame(settings.CustomRefreshPresets[0].Options, clone.CustomRefreshPresets[0].Options);

            clone.CustomRefreshPresets[0].Name = "Changed";
            var cloneProviders = clone.CustomRefreshPresets[0].Options.ProviderKeys as List<string>;
            cloneProviders.Add("Epic");

            Assert.AreEqual("Preset One", settings.CustomRefreshPresets[0].Name);
            Assert.AreEqual(1, settings.CustomRefreshPresets[0].Options.ProviderKeys.Count);
        }

        [TestMethod]
        public void SettingsExtensions_CopyFromAndClone_PreserveCustomRefreshPresets()
        {
            var gameId = Guid.NewGuid();
            var source = new PersistedSettings
            {
                CustomRefreshPresets = new List<CustomRefreshPreset>
                {
                    new CustomRefreshPreset
                    {
                        Name = "My Preset",
                        Options = new CustomRefreshOptions
                        {
                            ProviderKeys = new List<string> { "Steam" },
                            IncludeGameIds = new List<Guid> { gameId },
                            RespectUserExclusions = false
                        }
                    }
                }
            };

            var target = new PersistedSettings();
            target.CopyFrom(source);

            Assert.AreEqual(1, target.CustomRefreshPresets.Count);
            Assert.AreEqual("My Preset", target.CustomRefreshPresets[0].Name);
            Assert.AreNotSame(source.CustomRefreshPresets, target.CustomRefreshPresets);
            Assert.AreNotSame(source.CustomRefreshPresets[0].Options, target.CustomRefreshPresets[0].Options);

            var clone = SettingsExtensions.Clone(source);
            Assert.AreEqual(1, clone.CustomRefreshPresets.Count);
            Assert.AreEqual("My Preset", clone.CustomRefreshPresets[0].Name);
            Assert.AreNotSame(source.CustomRefreshPresets, clone.CustomRefreshPresets);
            Assert.AreNotSame(source.CustomRefreshPresets[0].Options, clone.CustomRefreshPresets[0].Options);
        }

        [TestMethod]
        public void SettingsExtensions_CopyFromAndClone_PreserveShowOverviewGameMetadata()
        {
            var source = new PersistedSettings
            {
                ShowOverviewGameMetadataPlatform = false,
                ShowOverviewGameMetadataPlaytime = false,
                ShowOverviewGameMetadataRegion = false
            };

            var target = new PersistedSettings
            {
                ShowOverviewGameMetadataPlatform = true,
                ShowOverviewGameMetadataPlaytime = true,
                ShowOverviewGameMetadataRegion = true
            };

            target.CopyFrom(source);
            Assert.IsFalse(target.ShowOverviewGameMetadataPlatform);
            Assert.IsFalse(target.ShowOverviewGameMetadataPlaytime);
            Assert.IsFalse(target.ShowOverviewGameMetadataRegion);

            var clone = SettingsExtensions.Clone(source);
            Assert.IsFalse(clone.ShowOverviewGameMetadataPlatform);
            Assert.IsFalse(clone.ShowOverviewGameMetadataPlaytime);
            Assert.IsFalse(clone.ShowOverviewGameMetadataRegion);
        }

        [TestMethod]
        public void PruneUnavailableSelections_RemovesUnavailableProvidersAndMissingGames()
        {
            var validGame = Guid.NewGuid();
            var missingGame = Guid.NewGuid();
            var options = new CustomRefreshOptions
            {
                ProviderKeys = new List<string> { "Steam", "Ghost", "STEAM" },
                IncludeGameIds = new List<Guid> { validGame, missingGame },
                ExcludeGameIds = new List<Guid> { missingGame },
                Scope = CustomGameScope.Explicit
            };

            var pruned = CustomRefreshPreset.PruneUnavailableSelections(
                options,
                new[] { "steam", "epic" },
                new[] { validGame },
                out var removedProviderCount,
                out var removedGameCount);

            Assert.AreEqual(1, removedProviderCount);
            Assert.AreEqual(2, removedGameCount);
            CollectionAssert.AreEquivalent(new[] { "Steam" }, pruned.ProviderKeys.ToList());
            CollectionAssert.AreEquivalent(new[] { validGame }, pruned.IncludeGameIds.ToList());
            Assert.AreEqual(0, pruned.ExcludeGameIds.Count);

            Assert.AreEqual(3, options.ProviderKeys.Count);
            Assert.AreEqual(2, options.IncludeGameIds.Count);
            Assert.AreEqual(1, options.ExcludeGameIds.Count);
        }

        [TestMethod]
        public void AchievementOrderHelper_TryReorder_SingleRowMove()
        {
            var source = new List<string> { "A", "B", "C", "D" };
            var moved = AchievementOrderHelper.TryReorder(
                source,
                new List<int> { 1 },
                targetIndex: 3,
                insertAfterTarget: true,
                out var reordered);

            Assert.IsTrue(moved);
            CollectionAssert.AreEqual(new List<string> { "A", "C", "D", "B" }, reordered);
        }

        [TestMethod]
        public void AchievementOrderHelper_TryReorder_MultiRowPreservesRelativeOrder()
        {
            var source = new List<string> { "A", "B", "C", "D", "E", "F" };
            var moved = AchievementOrderHelper.TryReorder(
                source,
                new List<int> { 1, 3 },
                targetIndex: 4,
                insertAfterTarget: false,
                out var reordered);

            Assert.IsTrue(moved);
            CollectionAssert.AreEqual(new List<string> { "A", "C", "B", "D", "E", "F" }, reordered);
        }

        [TestMethod]
        public void AchievementOrderHelper_TryReorder_DropBeforeAndAfter()
        {
            var source = new List<string> { "A", "B", "C", "D" };

            var movedBefore = AchievementOrderHelper.TryReorder(
                source,
                new List<int> { 0 },
                targetIndex: 2,
                insertAfterTarget: false,
                out var before);
            Assert.IsTrue(movedBefore);
            CollectionAssert.AreEqual(new List<string> { "B", "A", "C", "D" }, before);

            var movedAfter = AchievementOrderHelper.TryReorder(
                source,
                new List<int> { 0 },
                targetIndex: 2,
                insertAfterTarget: true,
                out var after);
            Assert.IsTrue(movedAfter);
            CollectionAssert.AreEqual(new List<string> { "B", "C", "A", "D" }, after);
        }

        [TestMethod]
        public void AchievementOrderHelper_TryReorder_NoOpSelfDrop()
        {
            var source = new List<string> { "A", "B", "C", "D" };
            var moved = AchievementOrderHelper.TryReorder(
                source,
                new List<int> { 1, 2 },
                targetIndex: 1,
                insertAfterTarget: false,
                out var reordered);

            Assert.IsFalse(moved);
            CollectionAssert.AreEqual(source, reordered);
        }

        [TestMethod]
        public void KindsAndTags_NormalizeAndFormatMultiValues()
        {
            Assert.AreEqual("Base|DLC", CategoryKindHelper.Normalize("dlc | base"));
            Assert.AreEqual("Multiplayer", AchievementTagHelper.Normalize("multiplayer, dlc | base"));
            Assert.AreEqual("Base, DLC", CategoryKindHelper.ToDisplayText("base|dlc"));
            Assert.AreEqual("Multiplayer", AchievementTagHelper.ToDisplayText("multiplayer"));
        }

        [TestMethod]
        public void CategoryLabels_DefaultFallbacks()
        {
            Assert.AreEqual("Default", AchievementCategoryHelper.NormalizeCategoryOrDefault(null));
            Assert.AreEqual("Label", AchievementCategoryHelper.NormalizeCategoryOrDefault(" Label "));
        }
    }
}
