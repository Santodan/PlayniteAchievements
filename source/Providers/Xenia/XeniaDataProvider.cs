using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Xenia
{
    internal sealed class XeniaDataProvider : IDataProvider
    {
        private readonly ILogger _logger;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly string _pluginUserDataPath;

        private XeniaScanner _scanner;
        private string _clientXeniaAccountPath;

        public XeniaDataProvider(ILogger logger, PlayniteAchievementsSettings settings, IPlayniteAPI playniteApi, string pluginUserDataPath)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _pluginUserDataPath = pluginUserDataPath ?? string.Empty;
            _clientXeniaAccountPath = _settings.Persisted.XeniaAccountPath?.Trim() ?? string.Empty;
        }
        public string ProviderName => ResourceProvider.GetString("LOCPlayAch_Provider_Xenia");
        public string ProviderKey => "Xenia";
        public string ProviderIconKey => "ProviderIconXenia";
        public string ProviderColorHex => "#2596BE";

        public bool IsAuthenticated 
        { 
            get 
            { 
                return File.Exists($"{_clientXeniaAccountPath}\\Account");
            } 
        }

        public bool IsCapable(Game game)
        {
            if (game == null || !IsAuthenticated) 
                return false;

            var src = game.Source?.Name ?? string.Empty;
            if (src.IndexOf("Xenia", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (game.Roms.Any(x => x.Path.EndsWith(".xex") || x.Path.EndsWith(".iso")))
                    return true;
            }


            return false;
        }

        public Task<RebuildPayload> RefreshAsync(IReadOnlyList<Game> gamesToRefresh, Action<Game> onGameStarting,
                                                Func<Game, GameAchievementData, Task> onGameCompleted, CancellationToken cancel)
        {    
            _scanner = new XeniaScanner(_logger, _settings, _pluginUserDataPath, _clientXeniaAccountPath);
            return _scanner.RefreshAsync(gamesToRefresh, onGameStarting, onGameCompleted, cancel);
        }
    }
}
