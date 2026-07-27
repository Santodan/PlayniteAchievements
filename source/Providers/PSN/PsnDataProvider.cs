using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.Overrides;
using PlayniteAchievements.Providers.Settings;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.GameCustomData;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.PSN
{
    internal sealed class PsnDataProvider : DataProviderBase<PsnSettings>, IDataProvider, IProviderOverride
    {
        public ProviderOverrideDescriptor OverrideDescriptor { get; } = ProviderOverrideDescriptor.Text(
            "LOCPlayAch_ManageAchievements_Overrides_ProviderValueLabel_PSN",
            raw => PsnNpCommIdHelper.TryNormalize(raw, out var commId)
                ? ProviderOverrideValidation.Valid(commId)
                : ProviderOverrideValidation.Invalid(
                    "LOCPlayAch_Menu_PsnNpCommId_InvalidId"));

        private readonly PsnSessionManager _sessionManager;
        private readonly PsnScanner _scanner;

        public PsnDataProvider(
            ILogger logger,
            PlayniteAchievementsSettings settings,
            IPlayniteAPI playniteApi,
            string pluginUserDataPath)
        {
            if (logger == null) throw new ArgumentNullException(nameof(logger));
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (playniteApi == null) throw new ArgumentNullException(nameof(playniteApi));
            if (string.IsNullOrWhiteSpace(pluginUserDataPath)) throw new ArgumentException("Plugin user data path is required.", nameof(pluginUserDataPath));

            _sessionManager = new PsnSessionManager(playniteApi, logger, pluginUserDataPath);

            _scanner = new PsnScanner(logger, settings, _sessionManager);
        }

        public string ProviderName
        {
            get
            {
                var value = ResourceProvider.GetString("LOCPlayAch_Provider_PSN");
                return string.IsNullOrWhiteSpace(value) ? "PlayStation" : value;
            }
        }

        public string ProviderKey => "PSN";

        public string ProviderIconKey => "ProviderIconPSN";

        public string ProviderColorHex => "#0070D1";

        public bool IsAuthenticated => _sessionManager.IsAuthenticated;

        public ISessionManager AuthSession => _sessionManager;

        public PlayniteAchievements.Models.Friends.IFriendsProvider Friends => null;

        public bool IsCapable(Game game)
        {
            if (game == null)
            {
                return false;
            }

            var id = (game.GameId ?? string.Empty).Trim();
            if (LooksLikePsnId(id))
            {
                return true;
            }

            var src = (game.Source?.Name ?? string.Empty).Trim();
            if (src.IndexOf("PlayStation", StringComparison.OrdinalIgnoreCase) >= 0 ||
                src.Equals("PSN", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return GameCustomDataLookup.TryGetProviderOverrideValue(game.Id, "PSN", out _);
        }

        public Task<RebuildPayload> RefreshAsync(
            IReadOnlyList<Game> gamesToRefresh,
            Action<Game> onGameStarting,
            Func<Game, GameAchievementData, Task> onGameCompleted,
            CancellationToken cancel)
        {
            return _scanner.RefreshAsync(gamesToRefresh, onGameStarting, onGameCompleted, cancel);
        }

        /// <inheritdoc />
        public ProviderSettingsViewBase CreateSettingsView() => new PsnSettingsView(_sessionManager);

        private static bool LooksLikePsnId(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return false;
            }

            if (id.StartsWith("CUSA", StringComparison.OrdinalIgnoreCase) ||
                id.StartsWith("PPSA", StringComparison.OrdinalIgnoreCase) ||
                id.StartsWith("NPWR", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return id.IndexOf("#CUSA", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   id.IndexOf("#PPSA", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   id.IndexOf("#NPWR", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}



