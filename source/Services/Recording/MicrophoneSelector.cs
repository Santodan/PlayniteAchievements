using System;
using System.Collections.Generic;
using Playnite.SDK;

namespace PlayniteAchievements.Services.Recording
{
    /// <summary>
    /// Chooses which input device the recorder mixes in when the user asks for a microphone.
    ///
    /// The default recording device is not always the one they mean: connecting a DualSense makes
    /// Windows switch the default to the pad's own microphone, which sits centimetres from the
    /// haptic actuators and records their buzz acoustically — a copy of the haptics that no
    /// render-side cancellation can reach, because it never went through the audio engine.
    ///
    /// A controller microphone is never selected. If no positively identified non-controller input
    /// exists, microphone capture is omitted; render-side cancellation cannot make that input safe.
    /// </summary>
    internal static class MicrophoneSelector
    {
        private static readonly object InventoryGate = new object();
        private static string _lastInventory;

        /// <summary>
        /// The safe device to record from, or null to omit microphone capture.
        /// </summary>
        public static EndpointIdentity TryChoose(ILogger logger)
        {
            try
            {
                var candidates = AudioEndpointEnumerator.EnumerateActive(AudioDataFlow.Capture);
                var inventory = new List<string>();

                var console = Match(
                    AudioEndpointEnumerator.TryGetDefaultEndpointId(
                        AudioDataFlow.Capture, AudioEndpointRole.Console),
                    candidates);
                var communications = Match(
                    AudioEndpointEnumerator.TryGetDefaultEndpointId(
                        AudioDataFlow.Capture, AudioEndpointRole.Communications),
                    candidates);

                foreach (var device in candidates)
                {
                    inventory.Add(
                        $"'{Describe(device)}'{(device.IsSame(console) ? " default" : string.Empty)}" +
                        $"{(device.IsSame(communications) ? " comms" : string.Empty)}" +
                        $"{(IsControllerMicrophone(device) ? " CONTROLLER" : string.Empty)}");
                }

                // In preference order: the default, the communications default, then anything else
                // present — each only if it is not a controller's own microphone.
                var chosen = FirstUsable(new[] { console, communications }) ?? FirstUsable(candidates);
                if (chosen == null && candidates.Count > 0)
                {
                    logger?.Warn(
                        "[Recording] No non-controller microphone is available; microphone capture " +
                        "is omitted so controller haptics cannot enter the clip acoustically.");
                }
                else if (chosen != null && !chosen.IsSame(console))
                {
                    logger?.Info(
                        $"[Recording] Recording from '{Describe(chosen)}' rather than the default input " +
                        $"'{Describe(console)}', which is a controller microphone and would record the pad's haptics.");
                }

                LogInventory(logger, inventory);
                return chosen;
            }
            catch (Exception ex)
            {
                logger?.Warn(
                    ex,
                    "[Recording] Input devices could not be enumerated; microphone capture is " +
                    "omitted rather than risk using a controller microphone.");
                return null;
            }
        }

        /// <summary>The first of <paramref name="preferred"/> that is present and not a controller.</summary>
        private static EndpointIdentity FirstUsable(IEnumerable<EndpointIdentity> preferred)
        {
            foreach (var device in preferred)
            {
                if (device != null && !IsControllerMicrophone(device))
                {
                    return device;
                }
            }

            return null;
        }

        /// <summary>
        /// The enumerated device with this id. The default-endpoint call reports an id rather than
        /// an entry of the inventory, so it is resolved back into one and the two never diverge.
        /// </summary>
        private static EndpointIdentity Match(string id, List<EndpointIdentity> present)
        {
            if (string.IsNullOrEmpty(id))
            {
                return null;
            }

            foreach (var candidate in present)
            {
                if (string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }

            return null;
        }

        private static bool IsControllerMicrophone(EndpointIdentity device)
        {
            return HapticEndpointClassifier.IsHapticEndpoint(
                RenderEndpointScan.IdentityCandidates(device),
                device.FriendlyName,
                device.DeviceFriendlyName);
        }

        private static void LogInventory(ILogger logger, List<string> inventory)
        {
            var line = string.Join(", ", inventory.ToArray());
            lock (InventoryGate)
            {
                if (string.Equals(line, _lastInventory, StringComparison.Ordinal))
                {
                    return;
                }

                _lastInventory = line;
            }

            logger?.Info("[Recording] Input devices: " + line);
        }

        private static string Describe(EndpointIdentity device)
        {
            return device == null ? "none" : device.Describe();
        }
    }
}
