using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers.EmuLibrary;
using PlayniteAchievements.Providers.Overrides;
using PlayniteAchievements.Providers.Settings;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.GameCustomData;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Xenia
{
    internal sealed class XeniaDataProvider : DataProviderBase<XeniaSettings>, IDataProvider, IProviderOverride
    {
        public ProviderOverrideDescriptor OverrideDescriptor { get; } = ProviderOverrideDescriptor.Text(
            "LOCPlayAch_ManageAchievements_Overrides_ProviderValueLabel_Xenia",
            raw => XeniaTitleIdHelper.TryNormalize(raw, out var titleId)
                ? ProviderOverrideValidation.Valid(titleId)
                : ProviderOverrideValidation.Invalid(
                    "LOCPlayAch_Menu_XeniaTitleId_InvalidId"));

        private readonly ILogger _logger;
        private readonly IPlayniteAPI _playniteApi;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly string _pluginUserDataPath;

        public XeniaDataProvider(ILogger logger, PlayniteAchievementsSettings settings, IPlayniteAPI playniteApi, string pluginUserDataPath)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _playniteApi = playniteApi;
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _pluginUserDataPath = pluginUserDataPath ?? string.Empty;
        }

        public string ProviderName => ResourceProvider.GetString("LOCPlayAch_Provider_Xenia");
        public string ProviderKey => "Xenia";
        public string ProviderIconKey => "ProviderIconXenia";
        public string ProviderColorHex => "#92C83E";
        public ISessionManager AuthSession => null;

        public PlayniteAchievements.Models.Friends.IFriendsProvider Friends => null;

        public bool IsAuthenticated
        {
            get
            {
                var accountPath = GetAccountPath();
                return !string.IsNullOrWhiteSpace(accountPath) &&
                       File.Exists(Path.Combine(accountPath, "Account"));
            }
        }

        public bool IsCapable(Game game)
        {
            if (game == null)
            {
                return false;
            }

            if (TryGetTitleIdOverride(game.Id, out _))
            {
                return true;
            }

            if (!HasSupportedRom(game))
            {
                return false;
            }

            if (UsesXeniaEmulator(game))
            {
                return true;
            }

            var src = game.Source?.Name ?? string.Empty;
            if (src.IndexOf("xenia", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            return game.Platforms?.Any(p => p.SpecificationId == "xbox360") == true;
        }

        private bool UsesXeniaEmulator(Game game)
        {
            if (game?.GameActions == null)
            {
                return false;
            }

            foreach (var action in game.GameActions)
            {
                if (action?.Type != GameActionType.Emulator || action.EmulatorId == Guid.Empty)
                {
                    continue;
                }

                var emulator = _playniteApi?.Database?.Emulators?.Get(action.EmulatorId);
                if (IsXeniaEmulator(emulator))
                {
                    return true;
                }
            }

            return false;
        }

        public Task<RebuildPayload> RefreshAsync(
            IReadOnlyList<Game> gamesToRefresh,
            Action<Game> onGameStarting,
            Func<Game, GameAchievementData, Task> onGameCompleted,
            CancellationToken cancel)
        {
            return new XeniaScanner(_logger, _playniteApi, ProviderSettings, _pluginUserDataPath, _settings)
                .RefreshAsync(gamesToRefresh, onGameStarting, onGameCompleted, cancel);
        }

        private string GetAccountPath()
        {
            return (ProviderSettings?.AccountPath ?? string.Empty).Trim();
        }

        private static bool IsXeniaEmulator(Emulator emulator)
        {
            if (emulator == null)
            {
                return false;
            }

            var builtInId = emulator.BuiltInConfigId ?? string.Empty;
            var name = emulator.Name ?? string.Empty;
            var installDir = emulator.InstallDir ?? string.Empty;

            return builtInId.IndexOf("xenia", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("xenia", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   installDir.IndexOf("xenia", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private bool HasSupportedRom(Game game)
        {
            var roms = game?.Roms;
            if (roms != null)
            {
                foreach (var rom in roms)
                {
                    if (IsSupportedRomPath(PathExpansion.ExpandGamePath(_playniteApi, game, rom?.Path)))
                    {
                        return true;
                    }
                }
            }

            // Uninstalled EmuLibrary games carry no rom entries; check the source file
            // decoded from the serialized EmuLibrary game id instead.
            return EmuLibraryPathResolver.TryResolveSourceFilePath(_playniteApi, game, out var emuLibrarySourceFile) &&
                   IsSupportedRomPath(emuLibrarySourceFile);
        }

        private static bool IsSupportedRomPath(string path)
        {
            path = (path ?? string.Empty).Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            var extension = Path.GetExtension(path) ?? string.Empty;
            return string.IsNullOrWhiteSpace(extension) ||
                   extension.Equals(".iso", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".xex", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool TryGetTitleIdOverride(Guid gameId, out string titleIdOverride)
        {
            return GameCustomDataLookup.TryGetXeniaTitleIdOverride(gameId, out titleIdOverride);
        }

        internal static bool TrySetTitleIdOverride(Guid gameId, string titleId, string gameName, Action persistSettingsForUi, ILogger logger)
        {
            if (!XeniaTitleIdHelper.TryNormalize(titleId, out var normalizedTitleId))
            {
                return false;
            }

            var customDataStore = PlayniteAchievementsPlugin.Instance?.GameCustomDataStore;
            if (customDataStore == null)
            {
                return false;
            }

            customDataStore.Update(gameId, customData =>
            {
                customData.ProviderOverride = new ProviderOverrideData
                {
                    ProviderKey = "Xenia",
                    Value = normalizedTitleId
                };
            });

            persistSettingsForUi?.Invoke();
            logger?.Info($"Set Xenia TitleID override for '{gameName}' to {normalizedTitleId}");
            return true;
        }

        internal static bool TryClearTitleIdOverride(Guid gameId, string gameName, Action persistSettingsForUi, ILogger logger)
        {
            var customDataStore = PlayniteAchievementsPlugin.Instance?.GameCustomDataStore;
            if (customDataStore == null ||
                !customDataStore.TryLoad(gameId, out var customData) ||
                customData?.ProviderOverride == null ||
                !string.Equals(customData.ProviderOverride.ProviderKey, "Xenia", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            customDataStore.Update(gameId, data =>
            {
                data.ProviderOverride = null;
            });

            XeniaScanner.ClearCachedTitleId(
                PlayniteAchievementsPlugin.Instance?.GetPluginUserDataPath(),
                gameId,
                logger);

            persistSettingsForUi?.Invoke();
            logger?.Info($"Cleared Xenia TitleID override for '{gameName}'");
            return true;
        }

        /// <inheritdoc />
        public IProviderSettings CreateDefaultSettings() => new XeniaSettings();

        /// <inheritdoc />
        public ProviderSettingsViewBase CreateSettingsView() => new XeniaSettingsView(_playniteApi);
    }
}


