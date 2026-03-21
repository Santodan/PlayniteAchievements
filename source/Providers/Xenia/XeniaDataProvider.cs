using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.Settings;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Xenia
{
    internal sealed class XeniaDataProvider : IDataProvider
    {
        private readonly ILogger _logger;
        private readonly IPlayniteAPI _playniteApi;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly string _pluginUserDataPath;
        private XeniaSettings _providerSettings;

        public XeniaDataProvider(ILogger logger, PlayniteAchievementsSettings settings, IPlayniteAPI playniteApi, string pluginUserDataPath)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _playniteApi = playniteApi;
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _pluginUserDataPath = pluginUserDataPath ?? string.Empty;
            _providerSettings = ProviderSettings.Load<XeniaSettings>();
        }

        public string ProviderName => ResourceProvider.GetString("LOCPlayAch_Provider_Xenia");
        public string ProviderKey => "Xenia";
        public string ProviderIconKey => "ProviderIconXenia";
        public string ProviderColorHex => "#92C83E";

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
            if (game == null || !HasSupportedRom(game))
            {
                return false;
            }

            if (UsesXeniaEmulator(game))
            {
                return true;
            }

            var src = game.Source?.Name ?? string.Empty;
            return src.IndexOf("xenia", StringComparison.OrdinalIgnoreCase) >= 0;
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
            return new XeniaScanner(_logger, _playniteApi, _providerSettings, _pluginUserDataPath)
                .RefreshAsync(gamesToRefresh, onGameStarting, onGameCompleted, cancel);
        }

        private string GetAccountPath()
        {
            return (_providerSettings?.AccountPath ?? string.Empty).Trim();
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
            if (roms == null)
            {
                return false;
            }

            foreach (var rom in roms)
            {
                var path = PathExpansion.ExpandGamePath(_playniteApi, game, rom?.Path);
                path = (path ?? string.Empty).Trim().Trim('"');
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                var extension = Path.GetExtension(path) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(extension) ||
                    extension.Equals(".iso", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".xex", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <inheritdoc />
        public IProviderSettings GetSettings() => _providerSettings;

        /// <inheritdoc />
        public IProviderSettings CreateDefaultSettings() => new XeniaSettings();

        /// <inheritdoc />
        public void ApplySettings(IProviderSettings settings)
        {
            if (settings is XeniaSettings xeniaSettings)
            {
                _providerSettings = xeniaSettings;
                xeniaSettings.Save();
            }
        }
    }
}
