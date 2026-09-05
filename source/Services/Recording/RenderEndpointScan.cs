using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Services.Recording
{
    /// <summary>Adapts a render endpoint's read identity to the pure controller-endpoint classifier.</summary>
    internal static class RenderEndpointScan
    {
        private static readonly object CacheGate = new object();
        private static readonly Dictionary<string, bool> Cache =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The DualSense's shared-mode layout: 4 channels as front and back pairs.</summary>
        private const int ControllerLayoutChannels = 4;
        private const uint ControllerLayoutMask = 0x33;

        /// <summary>
        /// Returns whether an endpoint belongs to a controller that carries audio haptics: either the
        /// classifier recognises its hardware id or name, or the endpoint exposes the controller
        /// layout itself (<see cref="HasControllerLayout"/>), so a pad the classifier has never heard
        /// of is still caught. Endpoint identity is immutable, so the relatively expensive
        /// property-store sweep is cached by id.
        /// </summary>
        internal static bool IsHapticEndpoint(EndpointIdentity endpoint)
        {
            if (endpoint == null)
            {
                return false;
            }

            var id = endpoint.Id ?? string.Empty;
            lock (CacheGate)
            {
                if (Cache.TryGetValue(id, out var cached))
                {
                    return cached;
                }
            }

            var verdict = HapticEndpointClassifier.IsHapticEndpoint(
                IdentityCandidates(endpoint),
                endpoint.FriendlyName,
                endpoint.DeviceFriendlyName) || HasControllerLayout(endpoint);
            lock (CacheGate)
            {
                Cache[id] = verdict;
            }

            return verdict;
        }

        /// <summary>
        /// Whether the endpoint's mix format is the 4-channel front-and-back-pairs layout a
        /// DualSense exposes. Quadraphonic speaker outputs share it and are treated the same way:
        /// their back pair leaves clips while they are active, which is the cheaper mistake.
        /// </summary>
        internal static bool HasControllerLayout(EndpointIdentity endpoint)
        {
            return endpoint != null &&
                endpoint.MixChannels == ControllerLayoutChannels &&
                endpoint.MixChannelMask == ControllerLayoutMask;
        }

        /// <summary>
        /// The endpoint's instance id first, then every other string its property store holds:
        /// which property carries the vendor/product pair varies by driver.
        /// </summary>
        internal static IEnumerable<string> IdentityCandidates(EndpointIdentity endpoint)
        {
            var candidates = new List<string>();
            Collect(candidates, endpoint.InstanceId);
            foreach (var value in endpoint.PropertyStrings)
            {
                Collect(candidates, value);
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
    }
}
