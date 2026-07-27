using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using PlayniteAchievements.Models.Friends;
using PlayniteAchievements.Providers.Exophase;

namespace PlayniteAchievements.Providers.Tests
{
    [TestClass]
    public class ExophaseSettingsTests
    {
        [TestMethod]
        public void Constructor_DefaultsManagedProvidersToStubOnlyPlatforms()
        {
            var settings = new ExophaseSettings();

            Assert.IsTrue(settings.ManagedProviders.Contains("android"));
            Assert.IsTrue(settings.ManagedProviders.Contains("apple"));
            Assert.IsTrue(settings.ManagedProviders.Contains("ubisoft"));

            Assert.IsFalse(settings.ManagedProviders.Contains("blizzard"));
            Assert.IsFalse(settings.ManagedProviders.Contains("origin"));
            Assert.IsFalse(settings.ManagedProviders.Contains("ea"));
            Assert.IsFalse(settings.ManagedProviders.Contains("steam"));
            Assert.IsFalse(settings.ManagedProviders.Contains("gog"));
            Assert.IsFalse(settings.ManagedProviders.Contains("epic"));
            Assert.IsFalse(settings.ManagedProviders.Contains("psn"));
            Assert.IsFalse(settings.ManagedProviders.Contains("xbox"));
            Assert.IsFalse(settings.ManagedProviders.Contains("retro"));
        }

        [TestMethod]
        public void DeserializeFromJson_WhenManagedProvidersMissing_KeepsDefaultStubPlatforms()
        {
            var settings = new ExophaseSettings();

            settings.DeserializeFromJson("{\"IsEnabled\":true}");

            Assert.IsTrue(settings.ManagedProviders.Contains("android"));
            Assert.IsTrue(settings.ManagedProviders.Contains("apple"));
            Assert.IsTrue(settings.ManagedProviders.Contains("ubisoft"));
            Assert.IsFalse(settings.ManagedProviders.Contains("blizzard"));
            Assert.IsFalse(settings.ManagedProviders.Contains("origin"));
            Assert.IsFalse(settings.ManagedProviders.Contains("ea"));
        }

        [TestMethod]
        public void DeserializeFromJson_WhenOriginManaged_PreservesOrigin()
        {
            var settings = new ExophaseSettings();

            settings.DeserializeFromJson("{\"ManagedProviders\":[\"origin\",\"android\"]}");

            Assert.IsTrue(settings.ManagedProviders.Contains("origin"));
            Assert.IsTrue(settings.ManagedProviders.Contains("android"));
        }

        [TestMethod]
        public void DeserializeFromJson_WhenManagedProvidersPresent_ReplacesDefaultPlatforms()
        {
            var settings = new ExophaseSettings();

            settings.DeserializeFromJson("{\"ManagedProviders\":[\"steam\"]}");

            Assert.IsTrue(settings.ManagedProviders.Contains("steam"));
            Assert.IsFalse(settings.ManagedProviders.Contains("origin"));
            Assert.IsFalse(settings.ManagedProviders.Contains("blizzard"));
            Assert.IsFalse(settings.ManagedProviders.Contains("android"));
            Assert.IsFalse(settings.ManagedProviders.Contains("apple"));
            Assert.IsFalse(settings.ManagedProviders.Contains("ubisoft"));
        }

        [TestMethod]
        public void SerializeToJson_DefaultManagedProviders_DoesNotIncludeSteam()
        {
            var settings = new ExophaseSettings();

            var json = JObject.Parse(settings.SerializeToJson());
            var managedProviders = json["ManagedProviders"].ToObject<List<string>>();

            CollectionAssert.DoesNotContain(managedProviders, "steam");
            CollectionAssert.Contains(managedProviders, "android");
            CollectionAssert.Contains(managedProviders, "apple");
            CollectionAssert.Contains(managedProviders, "ubisoft");
        }

        [TestMethod]
        public void DeserializeFromJson_WhenSteamManaged_PreservesExplicitSteamManagement()
        {
            var settings = new ExophaseSettings();

            settings.DeserializeFromJson("{\"ManagedProviders\":[\"steam\",\"android\"]}");

            Assert.IsTrue(settings.ManagedProviders.Contains("steam"));
            Assert.IsTrue(settings.ManagedProviders.Contains("android"));
        }

        [TestMethod]
        public void CopyFrom_RemovesManagedProvidersMissingFromSource()
        {
            var target = new ExophaseSettings
            {
                ManagedProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "steam",
                    "origin"
                }
            };
            var source = new ExophaseSettings
            {
                ManagedProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "origin"
                }
            };

            target.CopyFrom(source);

            Assert.IsFalse(target.ManagedProviders.Contains("steam"));
            Assert.IsTrue(target.ManagedProviders.Contains("origin"));
        }

        [TestMethod]
        public void DeserializeFromJson_ManagedProvidersRemainCaseInsensitive()
        {
            var settings = new ExophaseSettings();

            settings.DeserializeFromJson("{\"ManagedProviders\":[\"Steam\"]}");

            Assert.IsTrue(settings.ManagedProviders.Contains("steam"));
        }

        [TestMethod]
        public void DeserializeFromJson_EaAliasNormalizesToOrigin()
        {
            var settings = new ExophaseSettings();

            settings.DeserializeFromJson("{\"ManagedProviders\":[\"ea\"]}");

            Assert.IsTrue(settings.ManagedProviders.Contains("origin"));
            Assert.IsFalse(settings.ManagedProviders.Contains("ea"));
        }

        [TestMethod]
        public void AddOrUpdateFriend_DefaultsToNoPlatforms()
        {
            var settings = new ExophaseSettings();

            var added = settings.AddOrUpdateFriend(" PureRuby87 ");

            Assert.IsTrue(added);
            Assert.AreEqual(1, settings.Friends.Count);
            Assert.AreEqual("PureRuby87", settings.Friends[0].Username);
            Assert.AreEqual(0, settings.Friends[0].SelectedPlatforms.Count);
        }

        [TestMethod]
        public void AddOrUpdateFriend_DuplicateUsernamesAreCaseInsensitive()
        {
            var settings = new ExophaseSettings();

            Assert.IsTrue(settings.AddOrUpdateFriend("PureRuby87"));
            Assert.IsFalse(settings.AddOrUpdateFriend(" pureruby87 "));

            Assert.AreEqual(1, settings.Friends.Count);
            Assert.AreEqual("pureruby87", settings.Friends[0].Username);
        }

        [TestMethod]
        public void FriendsSetter_NormalizesSelectedPlatforms()
        {
            var settings = new ExophaseSettings
            {
                Friends = new List<ExophaseFriendSettings>
                {
                    new ExophaseFriendSettings
                    {
                        Username = " Beer_Here ",
                        SelectedPlatforms = new List<string> { " Steam ", "steam", "PSN" }
                    }
                }
            };

            Assert.AreEqual("Beer_Here", settings.Friends[0].Username);
            CollectionAssert.AreEqual(new List<string> { "psn" }, settings.Friends[0].SelectedPlatforms);
        }

        [TestMethod]
        public void FriendGameMappings_NormalizesKeysAndDropsEmptyTargets()
        {
            var mappedId = Guid.NewGuid();
            var settings = new ExophaseSettings
            {
                FriendGameMappings = new Dictionary<string, Guid>
                {
                    [" PS5|Game-Slug "] = mappedId,
                    ["steam|empty"] = Guid.Empty
                }
            };

            Assert.AreEqual(1, settings.FriendGameMappings.Count);
            Assert.AreEqual(mappedId, settings.FriendGameMappings["ps5|game-slug"]);
        }
    }
}
