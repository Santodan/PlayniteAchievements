using System;
using System.Collections.Generic;
using NAudio.CoreAudioApi;

namespace PlayniteAchievements.Services.Recording
{
    /// <summary>Adapts an NAudio render endpoint to the pure controller-endpoint classifier.</summary>
    internal static class RenderEndpointScan
    {
        private static readonly object CacheGate = new object();
        private static readonly Dictionary<string, bool> Cache =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Returns whether an endpoint belongs to a controller that carries audio haptics. Endpoint
        /// identity is immutable, so the relatively expensive property-store sweep is cached by id.
        /// </summary>
        internal static bool IsHapticEndpoint(MMDevice device)
        {
            if (device == null)
            {
                return false;
            }

            var id = TryRead(() => device.ID) ?? string.Empty;
            lock (CacheGate)
            {
                if (Cache.TryGetValue(id, out var cached))
                {
                    return cached;
                }
            }

            var verdict = HapticEndpointClassifier.IsHapticEndpoint(
                IdentityCandidates(device),
                TryRead(() => device.FriendlyName),
                TryRead(() => device.DeviceFriendlyName));
            lock (CacheGate)
            {
                Cache[id] = verdict;
            }

            return verdict;
        }

        private static IEnumerable<string> IdentityCandidates(MMDevice device)
        {
            var candidates = new List<string>();
            Collect(candidates, TryRead(() => device.InstanceId));
            try
            {
                var properties = device.Properties;
                for (var index = 0; index < properties.Count; index++)
                {
                    try
                    {
                        var value = properties.GetValue(index).Value;
                        if (value is string text)
                        {
                            Collect(candidates, text);
                        }
                        else if (value is string[] many)
                        {
                            foreach (var entry in many)
                            {
                                Collect(candidates, entry);
                            }
                        }
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }

            return candidates;
        }

        private static void Collect(List<string> candidates, string value)
        {
            if (!string.IsNullOrEmpty(value) &&
                (value.IndexOf("VID_", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 value.IndexOf("VID&", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                candidates.Add(value);
            }
        }

        private static string TryRead(Func<string> read)
        {
            try
            {
                return read();
            }
            catch
            {
                return null;
            }
        }
    }
}
