using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Playnite.SDK.Models;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.Exophase;
using PlayniteAchievements.Providers.Settings;
using PlayniteAchievements.Providers.Xenia;

namespace PlayniteAchievements.Providers.Tests
{
    [TestClass]
    public class ProviderRegistryTests
    {
        [TestMethod]
        public void GetSettingsForEdit_BeforeBeginEdit_ReturnsDetachedCopy()
        {
            var context = CreateRegistryContext("old-path");

            var edited = (XeniaSettings)context.Registry.GetSettingsForEdit("Xenia");
            edited.AccountPath = "new-path";

            Assert.IsNotNull(edited);
            Assert.AreNotSame(context.LiveSettings, edited);
            Assert.AreEqual("old-path", context.LiveSettings.AccountPath);
            Assert.AreEqual("old-path", GetPersistedAccountPath(context.Settings));
        }

        [TestMethod]
        public void BeginEditSession_AfterLazyEditSession_KeepsExistingEditCopy()
        {
            var context = CreateRegistryContext("old-path");

            var edited = (XeniaSettings)context.Registry.GetSettingsForEdit("Xenia");
            edited.AccountPath = "new-path";

            context.Registry.BeginEditSession();

            var resumed = (XeniaSettings)context.Registry.GetSettingsForEdit("Xenia");

            Assert.AreSame(edited, resumed);
            Assert.AreEqual("new-path", resumed.AccountPath);
            Assert.AreEqual("old-path", context.LiveSettings.AccountPath);
        }

        [TestMethod]
        public void CommitEditSession_ThenPersistAllProviderSettings_PersistsEditedProviderSettings()
        {
            var context = CreateRegistryContext("old-path");

            var edited = (XeniaSettings)context.Registry.GetSettingsForEdit("Xenia");
            edited.AccountPath = "new-path";

            context.Registry.BeginEditSession();
            context.Registry.CommitEditSession(false);
            context.Registry.PersistAllProviderSettings(false);

            Assert.AreEqual("new-path", context.LiveSettings.AccountPath);
            Assert.AreEqual("new-path", context.Provider.Settings.AccountPath);
            Assert.AreEqual("new-path", GetPersistedAccountPath(context.Settings));
        }

        [TestMethod]
        public void CancelEditSession_AfterLazyEditSession_DiscardsEditsAndLeavesLiveSettingsUnchanged()
        {
            var context = CreateRegistryContext("old-path");

            var edited = (XeniaSettings)context.Registry.GetSettingsForEdit("Xenia");
            edited.AccountPath = "new-path";

            context.Registry.BeginEditSession();
            context.Registry.CancelEditSession();

            var reopened = (XeniaSettings)context.Registry.GetSettingsForEdit("Xenia");

            Assert.AreEqual("old-path", context.LiveSettings.AccountPath);
            Assert.AreEqual("old-path", context.Provider.Settings.AccountPath);
            Assert.AreEqual("old-path", reopened.AccountPath);
            Assert.AreEqual("old-path", GetPersistedAccountPath(context.Settings));
        }

        [TestMethod]
        public void CommitEditSession_RemovesCollectionEntriesFromEditedProviderSettings()
        {
            var settings = new PlayniteAchievementsSettings();
            var liveSettings = new ExophaseSettings
            {
                ManagedProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "steam",
                    "origin"
                }
            };

            var provider = new FakeProvider(liveSettings);
            var registry = new ProviderRegistry(settings, new[] { "Exophase" });
            RegisterProvider(registry, provider);
            registry.PersistAllProviderSettings(false);

            var edited = (ExophaseSettings)registry.GetSettingsForEdit("Exophase");
            edited.ManagedProviders.Remove("steam");

            registry.CommitEditSession(false);
            registry.PersistAllProviderSettings(false);

            var persistedManagedProviders = settings.Persisted.ProviderSettings["Exophase"]["ManagedProviders"]
                .ToObject<List<string>>();

            Assert.IsFalse(liveSettings.ManagedProviders.Contains("steam"));
            Assert.IsTrue(liveSettings.ManagedProviders.Contains("origin"));
            Assert.IsFalse(persistedManagedProviders.Contains("steam"));
            Assert.IsTrue(persistedManagedProviders.Contains("origin"));
        }

        [TestMethod]
        public void ProviderColorOverride_ValidRgbAndArgbValuesOverrideProviderDefault()
        {
            var context = CreateRegistryContext("path");
            context.Settings.Persisted.ProviderColorOverrides =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["xEnIa"] = "#123456"
                };

            Assert.IsTrue(context.Registry.TryGetProviderVisuals(
                "Xenia",
                out var iconKey,
                out var colorHex));
            Assert.AreEqual("ProviderIconXenia", iconKey);
            Assert.AreEqual("#123456", colorHex);
            Assert.AreEqual("#123456", ProviderRegistry.GetProviderColorHex("XENIA"));

            context.Settings.Persisted.ProviderColorOverrides["Xenia"] = "#80123456";
            Assert.AreEqual("#80123456", ProviderRegistry.GetProviderColorHex("Xenia"));
        }

        [TestMethod]
        public void ProviderColorOverride_InvalidOrBlankValuesFallBackToProviderDefault()
        {
            var context = CreateRegistryContext("path");
            var expected = context.Provider.ProviderColorHex;

            foreach (var invalidValue in new[] { null, string.Empty, "not-a-color", "#12" })
            {
                context.Settings.Persisted.ProviderColorOverrides =
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Xenia"] = invalidValue
                    };

                Assert.AreEqual(expected, ProviderRegistry.GetProviderColorHex("Xenia"));
            }

            Assert.AreEqual(
                "#777777",
                ProviderRegistry.GetProviderColorHex("UnknownProvider", "#777777"));
        }

        [TestMethod]
        public void ProviderColorOverrides_CloneAndCopyAreDeepAndResetRestoresEmptyDefaults()
        {
            var source = new PersistedSettings
            {
                ProviderColorOverrides = new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["Xenia"] = "#123456"
                }
            };

            var clone = source.Clone();
            var copy = new PersistedSettings();
            copy.CopyFrom(source);
            source.ProviderColorOverrides["Xenia"] = "#654321";

            Assert.AreEqual("#123456", clone.ProviderColorOverrides["xenia"]);
            Assert.AreEqual("#123456", copy.ProviderColorOverrides["XENIA"]);
            Assert.AreNotSame(source.ProviderColorOverrides, clone.ProviderColorOverrides);
            Assert.AreNotSame(source.ProviderColorOverrides, copy.ProviderColorOverrides);

            source.ResetDisplaySettingsToDefaults();
            Assert.AreEqual(0, source.ProviderColorOverrides.Count);
        }

        private static RegistryContext CreateRegistryContext(string initialAccountPath)
        {
            var settings = new PlayniteAchievementsSettings();
            var liveSettings = new XeniaSettings
            {
                AccountPath = initialAccountPath,
                IsEnabled = true
            };

            var provider = new FakeXeniaProvider(liveSettings);
            var registry = new ProviderRegistry(settings, new[] { "Xenia" });
            RegisterProvider(registry, provider);
            registry.PersistAllProviderSettings(false);

            return new RegistryContext(settings, registry, provider, liveSettings);
        }

        private static void RegisterProvider(ProviderRegistry registry, IDataProvider provider)
        {
            var registerMethod = typeof(ProviderRegistry).GetMethod(
                "RegisterProviderInternals",
                BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.IsNotNull(registerMethod, "ProviderRegistry.RegisterProviderInternals was not found.");
            registerMethod.Invoke(registry, new object[] { provider });
        }

        private static string GetPersistedAccountPath(PlayniteAchievementsSettings settings)
        {
            return settings?.Persisted?.ProviderSettings?["Xenia"]?["AccountPath"]?.ToString();
        }

        private sealed class RegistryContext
        {
            public RegistryContext(
                PlayniteAchievementsSettings settings,
                ProviderRegistry registry,
                FakeXeniaProvider provider,
                XeniaSettings liveSettings)
            {
                Settings = settings;
                Registry = registry;
                Provider = provider;
                LiveSettings = liveSettings;
            }

            public PlayniteAchievementsSettings Settings { get; }

            public ProviderRegistry Registry { get; }

            public FakeXeniaProvider Provider { get; }

            public XeniaSettings LiveSettings { get; }
        }

        private sealed class FakeXeniaProvider : IDataProvider
        {
            public FakeXeniaProvider(XeniaSettings settings)
            {
                Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            }

            public string ProviderName => "Xenia";

            public string ProviderKey => "Xenia";

            public string ProviderIconKey => "ProviderIconXenia";

            public string ProviderColorHex => "#92C83E";

            public bool IsAuthenticated => true;

            public ISessionManager AuthSession => null;
            public PlayniteAchievements.Models.Friends.IFriendsProvider Friends => null;

            public XeniaSettings Settings { get; }

            public bool IsCapable(Game game) => true;

            public Task<RebuildPayload> RefreshAsync(
                IReadOnlyList<Game> gamesToRefresh,
                Action<Game> onGameStarting,
                Func<Game, GameAchievementData, Task> onGameCompleted,
                CancellationToken cancel)
            {
                return Task.FromResult(new RebuildPayload());
            }

            public IProviderSettings GetSettings() => Settings;

            public void ApplySettings(IProviderSettings settings)
            {
                Settings.CopyFrom(settings);
            }

            public ProviderSettingsViewBase CreateSettingsView() => new FakeProviderSettingsView();
        }

        private sealed class FakeProvider : IDataProvider
        {
            public FakeProvider(ProviderSettingsBase settings)
            {
                Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            }

            public string ProviderName => ProviderKey;

            public string ProviderKey => Settings.ProviderKey;

            public string ProviderIconKey => $"ProviderIcon{ProviderKey}";

            public string ProviderColorHex => "#92C83E";

            public bool IsAuthenticated => true;

            public ISessionManager AuthSession => null;
            public PlayniteAchievements.Models.Friends.IFriendsProvider Friends => null;

            public ProviderSettingsBase Settings { get; }

            public bool IsCapable(Game game) => true;

            public Task<RebuildPayload> RefreshAsync(
                IReadOnlyList<Game> gamesToRefresh,
                Action<Game> onGameStarting,
                Func<Game, GameAchievementData, Task> onGameCompleted,
                CancellationToken cancel)
            {
                return Task.FromResult(new RebuildPayload());
            }

            public IProviderSettings GetSettings() => Settings;

            public void ApplySettings(IProviderSettings settings)
            {
                Settings.CopyFrom(settings);
            }

            public ProviderSettingsViewBase CreateSettingsView() => new FakeProviderSettingsView();
        }

        private sealed class FakeProviderSettingsView : ProviderSettingsViewBase
        {
        }
    }
}
