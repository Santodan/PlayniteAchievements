using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Playnite.SDK.Models;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers;
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

        private sealed class FakeProviderSettingsView : ProviderSettingsViewBase
        {
        }
    }
}
