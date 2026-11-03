using System;
using System.Collections.Generic;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.Local;
using PlayniteAchievements.Services.InGameMonitoring;
using PlayniteAchievements.Services.Logging;

namespace PlayniteAchievements.Services
{
    // All fork decisions live here; the monitor retains only the batch/complete hooks.
    internal sealed partial class InGameAchievementMonitor
    {
        private sealed class SantodanLocalPollingState
        {
            public bool StartupBaselineChecked;
            public readonly LocalPollingFingerprintGate Fingerprint = new LocalPollingFingerprintGate();
            public readonly LocalPollingDirectoryCache Directories = new LocalPollingDirectoryCache();
        }

        private sealed class SantodanLocalPollingBatch
        {
            public readonly List<GamePollState> Games = new List<GamePollState>();
            public readonly Dictionary<Guid, string> Fingerprints = new Dictionary<Guid, string>();
        }

        private SantodanLocalPollingBatch PrepareSantodanLocalPolling(List<GamePollState> games)
        {
            if (!(games[0].Provider is LocalSavesProvider provider)) return null;
            var enabled = ProviderRegistry.Settings<LocalSettings>()?.UseCustomLocalPolling == true;
            if (!enabled)
            {
                // Disabling must discard the baseline, including if re-enabled mid-session.
                foreach (var game in games)
                    game.CustomLocalPolling.Fingerprint.NeedsRefresh(false, null);
                PollingDebugLog.Write("pollingMechanism=upstream");
                return null;
            }

            var batch = new SantodanLocalPollingBatch();
            foreach (var game in games)
            {
                string fingerprint = null;
                if (!game.WaitingForLocalAchievementFile)
                {
                    try
                    {
                        using var stage = PollingDebugLog.Stage("santodan.local.source-fingerprint");
                        if (!provider.TryGetSantodanPollingSourceFingerprint(game.Game, out fingerprint,
                            ProviderRegistry.Settings<LocalSettings>()?.UseCustomPollingDirectoryCache == true
                                ? game.CustomLocalPolling.Directories : null))
                            fingerprint = null;
                    }
                    catch (Exception ex)
                    {
                        _logger?.Debug(ex, "[SantodanPolling] Source check failed; using the provider refresh.");
                        fingerprint = null;
                    }
                }

                TryReuseSantodanStartupBaseline(game, fingerprint);
                var refresh = game.CustomLocalPolling.Fingerprint.NeedsRefresh(true, fingerprint);
                PollingDebugLog.Write("game=" + game.Game.Id + " pollingMechanism=santodan" +
                    " fingerprintAvailable=" + (fingerprint != null) + " refresh=" + refresh);
                if (refresh)
                {
                    batch.Games.Add(game);
                    batch.Fingerprints[game.Game.Id] = fingerprint;
                }
            }
            return batch;
        }

        private void TryReuseSantodanStartupBaseline(GamePollState game, string fingerprint)
        {
            if (game.CustomLocalPolling.StartupBaselineChecked) return;
            game.CustomLocalPolling.StartupBaselineChecked = true;
            if (ProviderRegistry.Settings<LocalSettings>()?.ReuseCustomPollingStartupBaseline != true)
                return;
            var reused = false;
            try
            {
                var cached = _cacheManager?.LoadGameData(game.Game.Id.ToString());
                var plugin = PlayniteAchievementsPlugin.Instance;
                if (plugin != null && !game.WaitingForLocalAchievementFile &&
                    cached?.Achievements?.Count > 0 && !cached.ExcludedByUser &&
                    string.Equals(cached.ProviderKey, "Local", StringComparison.OrdinalIgnoreCase) &&
                    (!cached.PlayniteGameId.HasValue || cached.PlayniteGameId == game.Game.Id) &&
                    new SantodanStartupBaselineStore(plugin.GetPluginUserDataPath()).Matches(
                        game.Game.Id, cached.AppId, cached.LastUpdatedUtc, fingerprint, DateTime.UtcNow, game.SessionStartUtc))
                {
                    lock (_stateLock)
                    {
                        game.CachedSchema = cached;
                        game.Schedule.MarkPrimed();
                    }
                    game.CustomLocalPolling.Fingerprint.MarkRefreshed(fingerprint);
                    reused = true;
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[SantodanPolling] Startup baseline validation failed; using full refresh.");
            }
            PollingDebugLog.Write("game=" + game.Game.Id + " startupBaselineReused=" + reused);
        }

        private void CompleteSantodanLocalPolling(SantodanLocalPollingBatch batch, GamePollState game,
            GameAchievementData before, GameAchievementData after)
        {
            if (batch == null || after == null ||
                (before != null && after.LastUpdatedUtc == before.LastUpdatedUtc) ||
                _refreshRuntime?.GetLastRebuildOutcome()?.Faulted == true ||
                !batch.Fingerprints.TryGetValue(game.Game.Id, out var fingerprint)) return;

            // Keep the pre-read signature so changes during refresh are detected next time.
            game.CustomLocalPolling.Fingerprint.MarkRefreshed(fingerprint);
        }
    }
}
