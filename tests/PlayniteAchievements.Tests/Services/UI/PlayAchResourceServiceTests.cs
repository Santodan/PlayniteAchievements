using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.UI;

namespace PlayniteAchievements.Tests.Services.UI
{
    [TestClass]
    public class PlayAchResourceServiceTests
    {
        [TestMethod]
        public void ResourceDescriptors_DoesNotExposeDerivedChromeBrushes()
        {
            AssertNoDescriptor("PlayAch.Brush.Grid.HeaderGripper.Hover");
            AssertNoDescriptor("PlayAch.Brush.Grid.HeaderGripper.Pressed");
            AssertNoDescriptor("PlayAch.Brush.ScrollBar.Track");
            AssertNoDescriptor("PlayAch.Brush.ScrollBar.Thumb");
            AssertNoDescriptor("PlayAch.Brush.ScrollBar.Thumb.Hover");
            AssertNoDescriptor("PlayAch.Brush.ScrollBar.Thumb.Pressed");
        }

        [TestMethod]
        public void Apply_DerivesScrollbarBrushesFromCustomAppearanceBrushes()
        {
            var resources = new ResourceDictionary();
            var overrides = new Dictionary<string, ResourceOverrideSetting>(StringComparer.OrdinalIgnoreCase)
            {
                ["PlayAch.Brush.Surface"] = CreateBrushOverride("#FF101820"),
                ["PlayAch.Brush.GridSurface"] = CreateBrushOverride("#FF202830"),
                ["PlayAch.Brush.ControlBorder"] = CreateBrushOverride("#FF334455"),
                ["PlayAch.Brush.Glyph"] = CreateBrushOverride("#FF556677"),
                ["PlayAch.Brush.Accent"] = CreateBrushOverride("#FF778899"),
                ["PlayAch.Brush.ScrollBar.Thumb"] = CreateBrushOverride("#FFFFFFFF")
            };

            PlayAchResourceService.Apply(resources, overrides);

            AssertBrush(resources, "PlayAch.Brush.ScrollBar.Track", Color.FromArgb(0x00, 0x20, 0x28, 0x30));
            AssertBrush(resources, "PlayAch.Brush.ScrollBar.Thumb", Color.FromRgb(0x33, 0x44, 0x55));
            AssertBrush(resources, "PlayAch.Brush.ScrollBar.Thumb.Hover", Color.FromRgb(0x55, 0x66, 0x77));
            AssertBrush(resources, "PlayAch.Brush.ScrollBar.Thumb.Pressed", Color.FromRgb(0x77, 0x88, 0x99));
        }

        [TestMethod]
        public void Apply_DerivesHeaderGripperBrushesFromCustomAppearanceBrushes()
        {
            var resources = new ResourceDictionary();
            var overrides = new Dictionary<string, ResourceOverrideSetting>(StringComparer.OrdinalIgnoreCase)
            {
                ["PlayAch.Brush.Accent"] = CreateBrushOverride("#FF405060"),
                ["PlayAch.Brush.Selection"] = CreateBrushOverride("#FF708090"),
                ["PlayAch.Brush.Grid.HeaderGripper.Hover"] = CreateBrushOverride("#FFFFFFFF")
            };

            PlayAchResourceService.Apply(resources, overrides);

            AssertBrush(resources, "PlayAch.Brush.Grid.HeaderGripper.Hover", Color.FromRgb(0x40, 0x50, 0x60));
            AssertBrush(resources, "PlayAch.Brush.Grid.HeaderGripper.Pressed", Color.FromRgb(0x70, 0x80, 0x90));
        }

        [TestMethod]
        public void Apply_DerivesControlBackgroundAliasesFromCustomAppearanceBrushes()
        {
            var resources = new ResourceDictionary();
            var overrides = new Dictionary<string, ResourceOverrideSetting>(StringComparer.OrdinalIgnoreCase)
            {
                ["PlayAch.Brush.ControlSurface"] = CreateBrushOverride("#FF102030"),
                ["PlayAch.Brush.Selection"] = CreateBrushOverride("#FF405060")
            };

            PlayAchResourceService.Apply(resources, overrides);

            AssertBrush(resources, "PlayAch.Brush.Control.Background", Color.FromRgb(0x10, 0x20, 0x30));
            AssertBrush(resources, "PlayAch.Brush.Control.Background.Hover", Color.FromArgb(0x30, 0x40, 0x50, 0x60));
        }

        [TestMethod]
        public void Apply_DerivesChartAndProgressBrushesFromCustomAppearanceBrushes()
        {
            var resources = new ResourceDictionary();
            var overrides = new Dictionary<string, ResourceOverrideSetting>(StringComparer.OrdinalIgnoreCase)
            {
                ["PlayAch.Brush.Surface"] = CreateBrushOverride("#FF101820"),
                ["PlayAch.Brush.Border"] = CreateBrushOverride("#FF203040"),
                ["PlayAch.Brush.ControlBorder"] = CreateBrushOverride("#FF304050"),
                ["PlayAch.Brush.Accent"] = CreateBrushOverride("#FF708090")
            };

            PlayAchResourceService.Apply(resources, overrides);

            AssertBrush(resources, "PlayAch.Brush.Chart.Separator", Color.FromRgb(0x30, 0x40, 0x50));
            AssertBrush(resources, "PlayAch.Brush.Progress.Track", Color.FromRgb(0x10, 0x18, 0x20));
            AssertBrush(resources, "PlayAch.Brush.Progress.Fill", Color.FromArgb(0xB8, 0x70, 0x80, 0x90));
            AssertBrush(resources, "PlayAch.Brush.Progress.Border", Color.FromArgb(0xB8, 0x70, 0x80, 0x90));
        }

        [TestMethod]
        public void Apply_PublishesCompletedFillBrush()
        {
            var resources = new ResourceDictionary();

            PlayAchResourceService.Apply(resources, null);

            // The completed fill is always a plain CompletedStart -> CompletedEnd sweep;
            // the badge's rainbow default and highlight band never apply to the bar.
            var completed = resources["PlayAch.Brush.Progress.CompletedFill"] as LinearGradientBrush;
            Assert.IsNotNull(completed);
            Assert.IsTrue(completed.IsFrozen);
            Assert.AreEqual(new Point(1, 0), completed.EndPoint);
            Assert.AreEqual(2, completed.GradientStops.Count);
            Assert.AreEqual(ParseColor(RarityColorSettings.DefaultCompletedStart), completed.GradientStops.First().Color);
            Assert.AreEqual(ParseColor(RarityColorSettings.DefaultCompletedEnd), completed.GradientStops.Last().Color);
        }

        [TestMethod]
        public void Apply_UsesPersistedCompletedGradientColorsForCompletedFill()
        {
            var resources = new ResourceDictionary();
            var settings = new PersistedSettings
            {
                RarityColors = new RarityColorSettings
                {
                    CompletedStart = "#FFABCDEF",
                    CompletedEnd = "#FF123456"
                }
            };

            PlayAchResourceService.Apply(resources, null, settings);

            var completed = resources["PlayAch.Brush.Progress.CompletedFill"] as LinearGradientBrush;
            Assert.IsNotNull(completed);
            Assert.AreEqual(ParseColor("#FFABCDEF"), completed.GradientStops.First().Color);
            Assert.AreEqual(ParseColor("#FF123456"), completed.GradientStops.Last().Color);
        }

        [TestMethod]
        public void ApplyCompletedProgressFillResource_UsesCustomCompletedGradientWhenSupplied()
        {
            var resources = new ResourceDictionary();
            var settings = new PersistedSettings
            {
                RarityColors = new RarityColorSettings
                {
                    CompletedStart = "#FF112233",
                    CompletedEnd = "#FF445566"
                }
            };

            PlayniteAchievements.Models.Achievements.RarityAppearanceHelper
                .ApplyCompletedProgressFillResource(resources, settings);

            var gradient = resources["PlayAch.Brush.Progress.CompletedFill"] as LinearGradientBrush;
            Assert.IsNotNull(gradient);
            Assert.IsTrue(gradient.IsFrozen);
            Assert.AreEqual(ParseColor("#FF112233"), gradient.GradientStops.First().Color);
            Assert.AreEqual(ParseColor("#FF445566"), gradient.GradientStops.Last().Color);
        }

        [TestMethod]
        public void Apply_ResolvesTransparentOverrideToTransparentBrush()
        {
            var resources = new ResourceDictionary();
            var overrides = new Dictionary<string, ResourceOverrideSetting>(StringComparer.OrdinalIgnoreCase)
            {
                ["PlayAch.Brush.GridSurface"] = new ResourceOverrideSetting
                {
                    Mode = ResourceOverrideMode.Transparent,
                    CustomValue = PlayAchResourceService.TransparentValue
                }
            };

            PlayAchResourceService.Apply(resources, overrides);

            Assert.IsTrue(resources.Contains("PlayAch.Brush.GridSurface"));
            var brush = resources["PlayAch.Brush.GridSurface"] as SolidColorBrush;
            Assert.IsNotNull(brush);
            Assert.AreEqual((byte)0, brush.Color.A);
            Assert.IsTrue(brush.IsFrozen);
        }

        [TestMethod]
        public void Apply_TransparentOverrideIgnoresStaleCustomValue()
        {
            var resources = new ResourceDictionary();
            var overrides = new Dictionary<string, ResourceOverrideSetting>(StringComparer.OrdinalIgnoreCase)
            {
                ["PlayAch.Brush.GridSurface"] = new ResourceOverrideSetting
                {
                    Mode = ResourceOverrideMode.Transparent,
                    CustomValue = "#FFFFFFFF"
                }
            };

            PlayAchResourceService.Apply(resources, overrides);

            Assert.IsTrue(resources.Contains("PlayAch.Brush.GridSurface"));
            var brush = resources["PlayAch.Brush.GridSurface"] as SolidColorBrush;
            Assert.IsNotNull(brush);
            Assert.AreEqual((byte)0, brush.Color.A);
        }

        [TestMethod]
        public void CreateDefaultResourceOverrides_SeedsInlineSurfacesAsTransparent()
        {
            var defaults = PersistedSettings.CreateDefaultResourceOverrides();

            foreach (var key in new[]
            {
                "PlayAch.Brush.GridSurface",
                "PlayAch.Brush.ControlSurface",
                "PlayAch.Brush.WindowSurface"
            })
            {
                Assert.IsTrue(defaults.ContainsKey(key), key);
                Assert.AreEqual(ResourceOverrideMode.Transparent, defaults[key].Mode, key);
                Assert.AreEqual(PlayAchResourceService.TransparentValue, defaults[key].CustomValue, key);
            }

            // PopupSurface backs popout windows and must stay opaque, so it is never seeded.
            Assert.IsFalse(defaults.ContainsKey("PlayAch.Brush.PopupSurface"));
        }

        [TestMethod]
        public void ResourceOverrides_NormalizesStaleEntries()
        {
            var settings = new PersistedSettings
            {
                ResourceOverrides = new Dictionary<string, ResourceOverrideSetting>(StringComparer.OrdinalIgnoreCase)
                {
                    [" Follow "] = new ResourceOverrideSetting
                    {
                        Mode = ResourceOverrideMode.FollowPlaynite,
                        CustomValue = "#FFFFFFFF"
                    },
                    [" Custom "] = new ResourceOverrideSetting
                    {
                        Mode = ResourceOverrideMode.Custom,
                        CustomValue = "  #FF010203  "
                    },
                    [" Blank "] = new ResourceOverrideSetting
                    {
                        Mode = ResourceOverrideMode.Custom,
                        CustomValue = " "
                    },
                    [" Transparent "] = new ResourceOverrideSetting
                    {
                        Mode = ResourceOverrideMode.Transparent,
                        CustomValue = "#FFFFFFFF"
                    }
                }
            };

            Assert.IsFalse(settings.ResourceOverrides.ContainsKey("Follow"));
            Assert.IsFalse(settings.ResourceOverrides.ContainsKey("Blank"));
            Assert.AreEqual(ResourceOverrideMode.Custom, settings.ResourceOverrides["Custom"].Mode);
            Assert.AreEqual("#FF010203", settings.ResourceOverrides["Custom"].CustomValue);
            Assert.AreEqual(ResourceOverrideMode.Transparent, settings.ResourceOverrides["Transparent"].Mode);
            Assert.AreEqual(PlayAchResourceService.TransparentValue, settings.ResourceOverrides["Transparent"].CustomValue);
        }

        [TestMethod]
        public void PersistedSettings_ParameterlessCtor_HasNoSeededResourceOverrides()
        {
            // The parameterless ctor is the deserialization target. It must start empty so a
            // loaded config is taken verbatim and a removed ("Follow Playnite") override is not
            // re-introduced by a seeded default during the in-place dictionary merge.
            var settings = new PersistedSettings();

            Assert.AreEqual(0, settings.ResourceOverrides.Count);
        }

        [TestMethod]
        public void PlayniteAchievementsSettings_FreshWithPlugin_SeedsTransparentInlineSurfaces()
        {
            // The plugin-reference ctor is the genuine fresh-install path and seeds the
            // transparent inline-surface defaults.
            var settings = new PlayniteAchievementsSettings((PlayniteAchievements.PlayniteAchievementsPlugin)null);

            foreach (var key in new[]
            {
                "PlayAch.Brush.GridSurface",
                "PlayAch.Brush.ControlSurface"
            })
            {
                Assert.IsTrue(settings.Persisted.ResourceOverrides.ContainsKey(key), key);
                Assert.AreEqual(
                    ResourceOverrideMode.Transparent,
                    settings.Persisted.ResourceOverrides[key].Mode,
                    key);
            }

            // Fresh installs are already seeded, so the one-time migration never re-seeds them.
            Assert.IsTrue(settings.Persisted.InlineSurfaceTransparencySeeded);
        }

        [TestMethod]
        public void ResourceOverrides_RemovedKeyStaysRemovedThroughJsonRoundTrip()
        {
            // Reproduces the runtime load path: Newtonsoft (default ObjectCreationHandling.Auto)
            // populates the existing dictionary in place. A user who switched GridSurface to
            // "Follow Playnite" has it removed from the saved config; it must not reappear.
            var saved = new PersistedSettings
            {
                ResourceOverrides = new Dictionary<string, ResourceOverrideSetting>(StringComparer.OrdinalIgnoreCase)
                {
                    ["PlayAch.Brush.ControlSurface"] = new ResourceOverrideSetting
                    {
                        Mode = ResourceOverrideMode.Transparent,
                        CustomValue = PlayAchResourceService.TransparentValue
                    }
                }
            };

            var json = JsonConvert.SerializeObject(saved);
            var loaded = JsonConvert.DeserializeObject<PersistedSettings>(json);

            Assert.IsFalse(loaded.ResourceOverrides.ContainsKey("PlayAch.Brush.GridSurface"));
            Assert.IsTrue(loaded.ResourceOverrides.ContainsKey("PlayAch.Brush.ControlSurface"));
        }

        private static void AssertNoDescriptor(string resourceKey)
        {
            Assert.IsFalse(
                PlayAchResourceService.ResourceDescriptors.Any(item =>
                    string.Equals(item.ResourceKey, resourceKey, StringComparison.Ordinal)),
                resourceKey);
        }

        private static Color ParseColor(string hex)
        {
            return (Color)ColorConverter.ConvertFromString(hex);
        }

        private static ResourceOverrideSetting CreateBrushOverride(string color)
        {
            return new ResourceOverrideSetting
            {
                Mode = ResourceOverrideMode.Custom,
                CustomValue = color
            };
        }

        private static void AssertBrush(ResourceDictionary resources, string resourceKey, Color expected)
        {
            Assert.IsTrue(resources.Contains(resourceKey), resourceKey);
            var brush = resources[resourceKey] as SolidColorBrush;
            Assert.IsNotNull(brush, resourceKey);
            Assert.AreEqual(expected, brush.Color);
            Assert.IsTrue(brush.IsFrozen, resourceKey);
        }
    }
}
