using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.Settings;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.PSN
{
    internal sealed class PsnDataProvider : IDataProvider
    {
        private readonly PsnSessionManager _sessionManager;
        private readonly PsnScanner _scanner;
        private readonly PlayniteAchievementsSettings _settings;
        private PsnSettings _providerSettings;

        public PsnDataProvider(
            ILogger logger,
            PlayniteAchievementsSettings settings,
            PsnSessionManager sessionManager)
        {
            if (logger == null) throw new ArgumentNullException(nameof(logger));
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            _settings = settings;
            _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));

            _scanner = new PsnScanner(logger, _settings, _sessionManager);

            _providerSettings = ProviderSettings.Load<PsnSettings>();
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

        // Reuse an existing icon key so older sidebar icon templates keep rendering without additional XAML edits.
        public string ProviderIconKey => "ProviderIconPSN";

        public string ProviderColorHex => "#0070D1";

        public bool IsAuthenticated => _sessionManager.IsAuthenticated;

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

            return false;
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
        public IProviderSettings GetSettings() => _providerSettings;

        /// <inheritdoc />
        public IProviderSettings CreateDefaultSettings() => new PsnSettings();

        /// <inheritdoc />
        public void ApplySettings(IProviderSettings settings)
        {
            if (settings is PsnSettings psnSettings)
            {
                _providerSettings = psnSettings;
                psnSettings.Save();
            }
        }

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
