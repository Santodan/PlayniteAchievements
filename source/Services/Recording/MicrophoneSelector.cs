using System;
using System.Collections.Generic;
using NAudio.CoreAudioApi;
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
    /// So a controller microphone is used only when it is the only input there is: someone whose
    /// headset is plugged into the pad genuinely records through it, and taking that away would be
    /// worse than the noise. Anything else that is present wins over it.
    /// </summary>
    internal static class MicrophoneSelector
    {
        private static readonly object InventoryGate = new object();
        private static string _lastInventory;

        /// <summary>
        /// The device to record from, or null to leave the choice to NAudio's default. The caller
        /// owns the returned device for the life of the capture.
        /// </summary>
        public static MMDevice TryChoose(ILogger logger)
        {
            try
            {
                var enumerator = new MMDeviceEnumerator();
                var candidates = new List<MMDevice>();
                var inventory = new List<string>();
                foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                {
                    candidates.Add(device);
                }

                var console = TryGetDefault(enumerator, Role.Console);
                var communications = TryGetDefault(enumerator, Role.Communications);

                foreach (var device in candidates)
                {
                    inventory.Add(
                        $"'{Describe(device)}'{(IsSame(device, console) ? " default" : string.Empty)}" +
                        $"{(IsSame(device, communications) ? " comms" : string.Empty)}" +
                        $"{(IsControllerMicrophone(device) ? " CONTROLLER" : string.Empty)}");
                }

                // In preference order: the default, the communications default, then anything else
                // present — each only if it is not a controller's own microphone.
                var chosen = FirstUsable(new[] { console, communications }, candidates) ?? FirstUsable(candidates);
                if (chosen == null && candidates.Count > 0)
                {
                    // Only a controller microphone exists. Record it, and say why the clip may carry
                    // the pad's own rumble.
                    chosen = Match(console, candidates) ?? candidates[0];
                    logger?.Warn(
                        $"[Recording] The only microphone available is '{Describe(chosen)}' on a controller; " +
                        "its recording will carry the pad's haptics acoustically, which cannot be cancelled.");
                }
                else if (chosen != null && !IsSame(chosen, console))
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
                logger?.Debug(ex, "[Recording] Input devices could not be enumerated; using the default microphone.");
                return null;
            }
        }

        /// <summary>The first of <paramref name="preferred"/> that is present and not a controller.</summary>
        private static MMDevice FirstUsable(IEnumerable<MMDevice> preferred, List<MMDevice> present)
        {
            foreach (var device in preferred)
            {
                var match = Match(device, present);
                if (match != null && !IsControllerMicrophone(match))
                {
                    return match;
                }
            }

            return null;
        }

        private static MMDevice FirstUsable(List<MMDevice> present)
        {
            foreach (var device in present)
            {
                if (!IsControllerMicrophone(device))
                {
                    return device;
                }
            }

            return null;
        }

        /// <summary>
        /// The enumerated device with this id. The default-endpoint call returns its own object, and
        /// using it while the enumerated one is also alive would leave two references to the same
        /// endpoint with separate lifetimes.
        /// </summary>
        private static MMDevice Match(MMDevice device, List<MMDevice> present)
        {
            if (device == null)
            {
                return null;
            }

            foreach (var candidate in present)
            {
                if (IsSame(candidate, device))
                {
                    return candidate;
                }
            }

            return null;
        }

        private static bool IsControllerMicrophone(MMDevice device)
        {
            var identities = new List<string>();
            try
            {
                var properties = device.Properties;
                for (var i = 0; i < properties.Count; i++)
                {
                    try
                    {
                        if (properties.GetValue(i).Value is string text &&
                            (text.IndexOf("VID_", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             text.IndexOf("VID&", StringComparison.OrdinalIgnoreCase) >= 0))
                        {
                            identities.Add(text);
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

            return HapticEndpointClassifier.IsHapticEndpoint(
                identities, TryRead(() => device.FriendlyName), TryRead(() => device.DeviceFriendlyName));
        }

        private static bool IsSame(MMDevice left, MMDevice right)
        {
            return left != null && right != null &&
                   string.Equals(TryRead(() => left.ID), TryRead(() => right.ID), StringComparison.OrdinalIgnoreCase);
        }

        private static MMDevice TryGetDefault(MMDeviceEnumerator enumerator, Role role)
        {
            try
            {
                return enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, role)
                    ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, role)
                    : null;
            }
            catch
            {
                return null;
            }
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

        private static string Describe(MMDevice device)
        {
            if (device == null)
            {
                return "none";
            }

            return TryRead(() => device.FriendlyName) ?? TryRead(() => device.DeviceFriendlyName) ?? "unnamed";
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
