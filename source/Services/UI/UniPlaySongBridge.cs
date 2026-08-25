using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using Playnite.SDK;

namespace PlayniteAchievements.Services.UI
{
    /// <summary>
    /// The reflected UniPlaySong integration (v1.8.4+). Both calls are public, version-stamped API
    /// on the UniPlaySong plugin class, invoked by reflection so the two plugins stay independently
    /// versioned; a missing plugin or an older version degrades to the playnite:// URI and the
    /// capture-based chime path.
    /// </summary>
    internal static class UniPlaySongBridge
    {
        private static readonly Guid UniPlaySongPluginId =
            new Guid("a1b2c3d4-e5f6-7890-abcd-ef1234567890");

        /// <summary>The event source segment UniPlaySong maps to the achievement sound set.</summary>
        public const string EventSource = "playniteachievements";

        /// <summary>
        /// Asks UniPlaySong which file it would play for a tier, so export can mix that exact
        /// sound at the composited toast instead of separating a captured copy. Returns the file
        /// path, or null when it cannot be known (old UniPlaySong, unresolved, missing file).
        /// <paramref name="soundDisabled"/> is true only when UniPlaySong positively reports its
        /// achievement-sound master switch off — no live chime will play and none may be mixed.
        /// </summary>
        public static string TryResolveAchievementSound(
            IPlayniteAPI api,
            string tier,
            ILogger logger,
            out bool soundDisabled)
        {
            soundDisabled = false;
            try
            {
                var plugin = FindPlugin(api);
                var method = plugin?.GetType().GetMethod(
                    "ResolveAchievementSound", new[] { typeof(string) });
                if (method == null)
                {
                    return null;
                }

                var json = method.Invoke(plugin, new object[] { tier }) as string;
                if (string.IsNullOrWhiteSpace(json))
                {
                    return null;
                }

                var result = JObject.Parse(json);
                // apiVersion bumps only when an existing field's meaning changes; added fields
                // do not bump it. A newer meaning is refused rather than misread.
                if ((result.Value<int?>("apiVersion") ?? 1) > 1)
                {
                    logger?.Debug(
                        "UniPlaySong's achievement-sound API is newer than this plugin " +
                        "understands; using the capture-based chime fallback.");
                    return null;
                }

                if (result.Value<bool?>("ok") != true)
                {
                    logger?.Debug(
                        "UniPlaySong could not resolve the achievement sound: " +
                        $"{result.Value<string>("error") ?? "unknown"}.");
                    return null;
                }

                if (result.Value<bool?>("enabled") == false)
                {
                    soundDisabled = true;
                    return null;
                }

                var path = result.Value<string>("path");
                if (string.IsNullOrWhiteSpace(path) || result.Value<bool?>("exists") != true)
                {
                    return null;
                }

                return path;
            }
            catch (Exception ex)
            {
                logger?.Debug(ex, "UniPlaySong sound resolution failed.");
                return null;
            }
        }

        /// <summary>
        /// Fires the wave sound through UniPlaySong's in-process event (same handler, debounce,
        /// and settings gate as the playnite:// URI, without the shell's URI-resolution latency).
        /// Returns false when the call is unavailable so the caller can fall back to the URI.
        /// </summary>
        public static bool TryTriggerExternalEvent(IPlayniteAPI api, string tier, ILogger logger)
        {
            try
            {
                var plugin = FindPlugin(api);
                var method = plugin?.GetType().GetMethod(
                    "TriggerExternalEvent", new[] { typeof(string), typeof(string) });
                if (method == null)
                {
                    return false;
                }

                method.Invoke(plugin, new object[] { EventSource, tier });
                return true;
            }
            catch (Exception ex)
            {
                logger?.Debug(ex, "UniPlaySong external event failed.");
                return false;
            }
        }

        /// <summary>
        /// The volume UniPlaySong plays jingles at: its players set Volume = MusicVolume / 100
        /// (JingleService), and no separate jingle volume exists. Read best-effort from its
        /// settings file — the resolution API does not expose an effective volume yet — so the
        /// composited chime can be mixed at the loudness the user actually heard. Null when the
        /// file or key cannot be read; the caller then uses its fixed fallback gain.
        /// </summary>
        public static double? TryReadJingleVolume(IPlayniteAPI api, ILogger logger)
        {
            try
            {
                var root = api?.Paths?.ExtensionsDataPath;
                if (string.IsNullOrWhiteSpace(root))
                {
                    return null;
                }

                var configPath = System.IO.Path.Combine(
                    root, UniPlaySongPluginId.ToString(), "config.json");
                if (!System.IO.File.Exists(configPath))
                {
                    return null;
                }

                var volume = JObject.Parse(System.IO.File.ReadAllText(configPath))
                    .Value<double?>("MusicVolume");
                return volume.HasValue
                    ? (double?)(Math.Max(0, Math.Min(100, volume.Value)) / 100.0)
                    : null;
            }
            catch (Exception ex)
            {
                logger?.Debug(ex, "UniPlaySong volume could not be read.");
                return null;
            }
        }

        private static object FindPlugin(IPlayniteAPI api)
        {
            return api?.Addons?.Plugins?.FirstOrDefault(p => p?.Id == UniPlaySongPluginId);
        }
    }
}
