using System;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Remoting.Proxies;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers.Local;
using PlayniteAchievements.Services.UI;

namespace PlayniteAchievements.Tests.Services
{
    [TestClass]
    public class CustomNotificationPolicyTests
    {
        [DataTestMethod]
        [DataRow("Xbox", false, false, 0)]
        [DataRow("Local", false, false, 1)]
        [DataRow("Xbox", true, false, 0)]
        [DataRow("Xbox", false, true, 1)]
        public void BuiltPublisher_RespectsXboxOffWithCustomRendererEnabled(
            string provider, bool progress, bool preview, int expectedNotifications)
        {
            var settings = new PersistedSettings
            {
                EnableNotifications = true,
                EnableUnlockToasts = true,
                EnableProgressToasts = false
            };
            settings.SetProviderNotificationOverride("Xbox", new ProviderNotificationOverride
            {
                UnlockToasts = false,
                FriendUnlockToasts = false
            });

            var dispatched = Dispatch(settings, provider, progress, preview);
            Assert.AreEqual(expectedNotifications, dispatched.Popups,
                "Custom delivery must honor platform filters while preserving Local unlocks and explicit previews.");
            Assert.AreEqual(progress ? 0 : expectedNotifications, dispatched.InApp,
                "Progress remains overlay-only; own-unlock in-app notifications respect the same policy.");
        }

        [TestMethod]
        public void BuiltPublisher_ProgressDoesNotUseOwnUnlockSwitch()
        {
            var settings = new PersistedSettings { EnableNotifications = true, EnableProgressToasts = true };
            settings.SetProviderNotificationOverride("Xbox", new ProviderNotificationOverride { UnlockToasts = false });

            Assert.AreEqual(1, Dispatch(settings, "Xbox", progress: true).Popups);
            Assert.AreEqual(0, Dispatch(settings, "Xbox").Popups);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void BuiltPublisher_UsesGameOverrideEvenWithoutAGameObject(bool progress)
        {
            var settings = new PersistedSettings { EnableNotifications = false };
            var gameData = new GameCustomDataFile
            {
                NotificationBehaviorOverride = new GameNotificationBehaviorOverride
                {
                    EnableNotifications = true,
                    EnableUnlockToasts = true,
                    EnableProgressToasts = true
                }
            };
            Assert.AreEqual(1, Dispatch(settings, "Xbox", progress, gameData: gameData).Popups);
            gameData.NotificationBehaviorOverride.EnableNotifications = false;
            settings.EnableNotifications = true;
            Assert.AreEqual(0, Dispatch(settings, "Xbox", progress, gameData: gameData).Popups);
        }

        [TestMethod]
        public void BuiltPublisher_DirectLocalMonitorUsesGamePolicyWithoutEventContext()
        {
            var gameData = new GameCustomDataFile
            {
                NotificationBehaviorOverride = new GameNotificationBehaviorOverride { EnableNotifications = false }
            };
            var settings = new PersistedSettings { EnableNotifications = true, EnableUnlockToasts = true };
            Assert.AreEqual(0, Dispatch(settings, "Local", gameData: gameData, useGameObject: true).Popups);
            Assert.AreEqual(1, Dispatch(settings, "Local", useGameObject: true).Popups);
        }

        [TestMethod]
        public void Publisher_GatesAllLiveOutputsBeforeDispatch_WithoutGatingCaptureOwnership()
        {
            var publisher = ReadSource("Services", "NotificationPublisher.cs");
            var start = publisher.IndexOf("IReadOnlyList<AchievementUnlockNotificationItem> unlockedAchievements,", StringComparison.Ordinal);
            var dispatch = publisher.IndexOf("var configuredSettings =", start, StringComparison.Ordinal);
            var gate = publisher.Substring(start, dispatch - start);

            StringAssert.Contains(gate, "if (!isSettingsPreview)");
            StringAssert.Contains(gate, "ProviderNotificationPolicy.Resolve(");
            StringAssert.Contains(gate, "_settings?.Persisted");
            StringAssert.Contains(gate, "_loadGameData?.Invoke(");
            StringAssert.Contains(gate, "progressContext?.PlayniteGameId ?? game?.Id ?? Guid.Empty");
            StringAssert.Contains(gate, "progressContext?.IsProgressUpdate == true");
            StringAssert.Contains(gate, "effective.ProgressToasts");
            StringAssert.Contains(gate, "effective.UnlockToasts");
            StringAssert.Contains(gate, "if (!shouldNotify)");
            StringAssert.Contains(gate, "return;");
            StringAssert.Contains(gate, "Skipped custom achievement notification");

            var plugin = ReadSource("PlayniteAchievementsPlugin.cs");
            var handlerStart = plugin.IndexOf("private void HandlePolledAchievementUnlocked(", StringComparison.Ordinal);
            var handlerEnd = plugin.IndexOf("private void TryWarmCustomDataCache(", handlerStart, StringComparison.Ordinal);
            var handler = plugin.Substring(handlerStart, handlerEnd - handlerStart);
            StringAssert.Contains(handler, "args.SuppressStandardToast = true;");
            Assert.IsTrue(handler.IndexOf("NotifyAchievementUnlocked(args);", StringComparison.Ordinal) >
                handler.LastIndexOf("catch (Exception ex)", StringComparison.Ordinal),
                "Events must still reach captures and other subscribers even when custom output is filtered.");
            Assert.IsFalse(handler.Contains("ProviderNotificationPolicy"),
                "Filtering renderer ownership would change capture imagery and allow standard-toast fallback.");
            StringAssert.Contains(plugin,
                "loadGameData: gameId => _gameCustomDataStore != null && _gameCustomDataStore.TryLoad(gameId, out var data) ? data : null");
        }

        [DataTestMethod]
        [DataRow("Xbox")]
        [DataRow("Steam")]
        [DataRow("GOG")]
        public void PlatformUnlockOff_CustomRendererOn_DoesNotSilenceLocalOrCaptures(string provider)
        {
            var settings = new PersistedSettings
            {
                EnableNotifications = true,
                EnableUnlockToasts = true,
                EnableProgressToasts = true,
                EnableUnlockScreenshots = true,
                UnlockScreenshotWithToast = true,
                EnableUnlockRecordings = true
            };
            settings.SetProviderNotificationOverride(provider, new ProviderNotificationOverride
            {
                UnlockToasts = false,
                FriendUnlockToasts = false
            });

            var effective = ProviderNotificationPolicy.Resolve(settings, provider);
            Assert.IsFalse(effective.UnlockToasts);
            Assert.IsTrue(effective.ProgressToasts, "Progress uses its own switch, not the own-unlock switch.");
            Assert.IsTrue(effective.ScreenshotWithToast, "Notification filtering must not disable capture imagery.");
            Assert.IsTrue(effective.RecordingsEnabled);
            Assert.IsTrue(effective.Recordings);
            Assert.IsTrue(ProviderNotificationPolicy.Resolve(settings, "Local").UnlockToasts);

            settings.EnableProgressToasts = false;
            Assert.IsFalse(ProviderNotificationPolicy.Resolve(settings, provider).ProgressToasts,
                "The reported Xbox-Off plus Progress-Off configuration must suppress both types.");
            Assert.IsFalse(ProviderNotificationPolicy.Resolve(settings, provider).UnlockToasts);
            Assert.IsTrue(ProviderNotificationPolicy.Resolve(settings, "Local").UnlockToasts);
        }

        [TestMethod]
        public void GameBehaviorOverride_ReplacesGlobalAndPlatformForBothNotificationKinds()
        {
            var settings = new PersistedSettings { EnableNotifications = false };
            settings.SetProviderNotificationOverride("Xbox", new ProviderNotificationOverride
            {
                UnlockToasts = false,
                ProgressToasts = false
            });
            var game = new GameCustomDataFile
            {
                NotificationBehaviorOverride = new GameNotificationBehaviorOverride
                {
                    EnableNotifications = true,
                    EnableUnlockToasts = true,
                    EnableProgressToasts = true
                }
            };

            var effective = ProviderNotificationPolicy.Resolve(settings, "Xbox", game);
            Assert.IsTrue(effective.UnlockToasts);
            Assert.IsTrue(effective.ProgressToasts);

            game.NotificationBehaviorOverride.EnableNotifications = false;
            effective = ProviderNotificationPolicy.Resolve(settings, "Xbox", game);
            Assert.IsFalse(effective.UnlockToasts);
            Assert.IsFalse(effective.ProgressToasts);
        }

        // Exercise the built WPF publisher, not the test assembly's linked policy copy. Only
        // Playnite's host APIs are faked; no windows, native toasts, WebView or sound are opened.
        private static (int InApp, int Popups) Dispatch(PersistedSettings persisted, string provider,
            bool progress = false, bool preview = false, GameCustomDataFile gameData = null, bool useGameObject = false)
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies()
                .Single(item => item.GetName().Name == "PlayniteAchievements");
            var settingsType = assembly.GetType("PlayniteAchievements.Models.PlayniteAchievementsSettings", true);
            var settings = Activator.CreateInstance(settingsType);
            var persistedProperty = settingsType.GetProperty("Persisted");
            persistedProperty.SetValue(settings,
                JsonConvert.DeserializeObject(JsonConvert.SerializeObject(new
                {
                    persisted.EnableNotifications,
                    persisted.EnableUnlockToasts,
                    persisted.EnableProgressToasts,
                    persisted.ProviderNotificationOverrides
                }), persistedProperty.PropertyType));
            var notifications = 0;
            var popups = 0;
            var logger = new HostProxy(typeof(ILogger), call =>
            {
                if (call.Args.OfType<string>().Any(text => text.Contains("[LocalOverlay] Dispatching unlock popup")))
                {
                    popups++;
                }
                return null;
            }).GetTransparentProxy();
            var notificationApiType = typeof(IPlayniteAPI).GetProperty("Notifications").PropertyType;
            var notificationApi = new HostProxy(notificationApiType, call =>
            {
                Assert.AreEqual("Add", call.MethodName);
                notifications++;
                return null;
            }).GetTransparentProxy();
            var api = new HostProxy(typeof(IPlayniteAPI), call =>
            {
                switch (call.MethodName)
                {
                    case "get_MainView": return null;
                    case "get_Notifications": return notificationApi;
                    default: throw new InvalidOperationException("Unexpected host call: " + call.MethodName);
                }
            }).GetTransparentProxy();
            var publisherType = assembly.GetType("PlayniteAchievements.Services.NotificationPublisher", true);
            var constructor = publisherType.GetConstructors().Single();
            var gameId = Guid.NewGuid();
            Delegate loadGameData = null;
            if (gameData != null)
            {
                var resolver = constructor.GetParameters().SingleOrDefault(parameter => parameter.Name == "loadGameData");
                Assert.IsNotNull(resolver, "Live publisher needs the same per-game policy data as the standard renderer.");
                var dataType = resolver.ParameterType.GetGenericArguments()[1];
                var data = JsonConvert.DeserializeObject(JsonConvert.SerializeObject(gameData), dataType);
                var idParameter = Expression.Parameter(typeof(Guid));
                loadGameData = Expression.Lambda(resolver.ParameterType, Expression.Condition(
                    Expression.Equal(idParameter, Expression.Constant(gameId)),
                    Expression.Constant(data, dataType), Expression.Constant(null, dataType)), idParameter).Compile();
            }
            var constructorArgs = constructor.GetParameters().Select(parameter =>
                parameter.Name == "api" ? api : parameter.Name == "settings" ? settings :
                parameter.Name == "logger" ? logger : parameter.Name == "loadGameData" ? loadGameData : null).ToArray();
            var publisher = constructor.Invoke(constructorArgs);

            var method = publisherType.GetMethods().Single(item => item.Name == "ShowLocalAchievementUnlocked" &&
                item.GetParameters()[1].Name == "unlockedAchievements");
            var itemType = method.GetParameters()[1].ParameterType.GetGenericArguments()[0];
            var items = Array.CreateInstance(itemType, 1);
            items.SetValue(Activator.CreateInstance(itemType,
                new object[] { "PC Game Pass unlock", null, null, null, null, null }), 0);
            var local = new LocalSettings
            {
                EnableActiveGameMonitoring = true,
                EnableInAppUnlockNotifications = true,
                EnableWindowsToastNotifications = false,
                UnlockNotificationDeliveryMode = LocalUnlockNotificationDeliveryMode.WindowsToast
            };
            var arguments = method.GetParameters().Select(parameter =>
            {
                switch (parameter.Name)
                {
                    case "gameName": return "PC Game Pass game";
                    case "unlockedAchievements": return (object)items;
                    case "notificationProviderKey": return provider;
                    case "game": return useGameObject ? new Game { Id = gameId } : null;
                    case "overrideLocalSettings":
                        return JsonConvert.DeserializeObject(JsonConvert.SerializeObject(local), parameter.ParameterType);
                    case "progressContext":
                        return useGameObject ? null : JsonConvert.DeserializeObject(JsonConvert.SerializeObject(new
                        {
                            PlayniteGameId = gameId, IsProgressUpdate = progress
                        }), parameter.ParameterType);
                    case "isSettingsPreview": return preview;
                    default: return null;
                }
            }).ToArray();
            method.Invoke(publisher, arguments);
            return (notifications, popups);
        }

        private sealed class HostProxy : RealProxy
        {
            private readonly Func<IMethodCallMessage, object> _invoke;

            public HostProxy(Type type, Func<IMethodCallMessage, object> invoke) : base(type)
            {
                _invoke = invoke;
            }

            public override IMessage Invoke(IMessage message)
            {
                var call = (IMethodCallMessage)message;
                return new ReturnMessage(_invoke(call), null, 0, call.LogicalCallContext, call);
            }
        }

        private static string ReadSource(params string[] parts)
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                var path = Path.Combine(directory.FullName, "source", Path.Combine(parts));
                if (File.Exists(path))
                {
                    return File.ReadAllText(path);
                }

                directory = directory.Parent;
            }

            Assert.Fail("Repository source not found: " + Path.Combine(parts));
            return null;
        }
    }
}
