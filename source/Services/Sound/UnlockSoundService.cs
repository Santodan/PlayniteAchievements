using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.UI;

namespace PlayniteAchievements.Services.Sound
{
    /// <summary>What one wave played, so the recorder can mix the same file at the same level.</summary>
    internal sealed class UnlockSoundPlayback
    {
        public UnlockSoundPlayback(int id, DateTime sentUtc, ResolvedUnlockSound sound, double gain)
        {
            Id = id;
            SentUtc = sentUtc;
            Sound = sound;
            Gain = gain;
        }

        /// <summary>The host's play id; <see cref="UnlockSoundService.TryGetAudibleOnsetUtc"/> resolves it later.</summary>
        public int Id { get; }

        /// <summary>When the plugin asked for the sound (the launch moment, not the audible onset).</summary>
        public DateTime SentUtc { get; }
        public ResolvedUnlockSound Sound { get; }
        public double Gain { get; }
        public string FilePath => Sound?.Path;
    }

    /// <summary>
    /// The one object the notification service depends on for sound: reads the settings, resolves
    /// a tier to a file, keeps the sound host alive and preloaded, and reports what played.
    /// </summary>
    internal sealed class UnlockSoundService : IDisposable
    {
        private readonly PlayniteAchievementsSettings _settings;
        private readonly UnlockSoundResolver _resolver;
        private readonly UnlockSoundHost _host;
        private readonly ILogger _logger;
        private readonly object _gate = new object();
        private bool _disposed;

        public UnlockSoundService(
            PlayniteAchievementsSettings settings,
            UnlockSoundResolver resolver,
            string pluginInstallDirectory,
            ILogger logger)
        {
            _settings = settings;
            _resolver = resolver;
            _logger = logger;
            var executable = string.IsNullOrWhiteSpace(pluginInstallDirectory)
                ? null
                : Path.Combine(pluginInstallDirectory, SoundHostProtocol.ExecutableName);
            _host = new UnlockSoundHost(executable, logger);
        }

        public UnlockSoundResolver Resolver => _resolver;

        /// <summary>The sound host's pid for the recorder's reference capture; null when it is down.</summary>
        public int? HostProcessId => _host.ProcessId;

        private bool Enabled
        {
            get
            {
                var persisted = _settings?.Persisted;
                return persisted != null && persisted.EnableNotifications && persisted.EnableUnlockSounds;
            }
        }

        /// <summary>
        /// Starts the host and preloads the resolved set when sounds are enabled; stops the host
        /// when they are not. Idempotent, so it runs on startup and on every settings save.
        /// </summary>
        public void ApplySettings()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                if (!Enabled)
                {
                    // A disabled setting leaves no helper process behind; the next enable (or a
                    // Test button press) launches it again.
                    _host.Shutdown();
                    return;
                }

                if (_host.TryStart())
                {
                    _host.Preload(ResolvedPaths());
                }
            }
        }

        /// <summary>
        /// Plays the tier's sound at the configured volume. Null when sounds are off, nothing
        /// resolved, or the host is unavailable. <paramref name="force"/> plays even when the
        /// master switch is off, for the settings page's Test button.
        /// </summary>
        public UnlockSoundPlayback Play(UnlockSoundTier tier, bool force = false)
        {
            lock (_gate)
            {
                if (_disposed || (!force && !Enabled))
                {
                    return null;
                }

                var resolved = _resolver.Resolve(tier);
                if (resolved?.Path == null)
                {
                    return null;
                }

                var gain = (_settings?.Persisted?.UnlockSoundVolumePercent ?? 0) / 100.0;
                var sentUtc = _host.Play(resolved.Path, gain, out var id);
                return sentUtc.HasValue ? new UnlockSoundPlayback(id, sentUtc.Value, resolved, gain) : null;
            }
        }

        /// <summary>The measured audible onset of a played sound, once the host has reported it.</summary>
        public DateTime? TryGetAudibleOnsetUtc(int playbackId)
        {
            return _host.TryGetAudibleOnsetUtc(playbackId);
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
            }

            _host.Dispose();
        }

        private IReadOnlyList<string> ResolvedPaths()
        {
            return _resolver.ResolveAll()
                .Select(r => r.Path)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
