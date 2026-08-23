using System;
using System.Collections.Generic;
using NAudio.CoreAudioApi;
using Playnite.SDK;

namespace PlayniteAchievements.Services.Recording
{
    /// <summary>One render endpoint the recorder cares about, reduced to what it needs.</summary>
    internal sealed class HapticEndpointInfo
    {
        /// <summary>The endpoint id, which is also the device interface path ActivateAudioInterfaceAsync takes.</summary>
        public string DeviceId;

        public string Name;
    }

    /// <summary>
    /// Finds the controller audio endpoints present on this machine, so the recorder can capture
    /// them as a cancellation reference (see <see cref="AudioLoopbackRecorder"/>). Process loopback
    /// mixes every endpoint a process renders to, so a game's haptic waveform is inside the clip's
    /// audio; the only way to identify it is to capture that endpoint separately.
    ///
    /// The default render endpoint is never reported: if the user is listening through the
    /// controller's headphone jack, that endpoint carries audio they want kept.
    ///
    /// Every call is best-effort. A missing or unhappy audio stack reports nothing, which leaves
    /// the recorder on exactly the path it takes today.
    /// </summary>
    internal static class RenderEndpointScan
    {
        // The recorder re-scans for the life of a session (a pad can be connected late, or
        // re-enumerate), so the inventory is logged only when it changes and each endpoint's
        // identity is read from its property store once.
        private static readonly object InventoryGate = new object();
        private static readonly Dictionary<string, EndpointVerdict> Classified =
            new Dictionary<string, EndpointVerdict>(StringComparer.OrdinalIgnoreCase);
        private static string _lastInventory;

        public static IReadOnlyList<HapticEndpointInfo> FindHapticEndpoints(ILogger logger)
        {
            return FindHapticEndpoints(logger, out _, out _);
        }

        /// <summary>
        /// The endpoint list plus states a fail-closed recorder must distinguish from an ordinary
        /// empty result: an incomplete scan and a controller that is also the default output.
        /// </summary>
        public static IReadOnlyList<HapticEndpointInfo> FindHapticEndpoints(
            ILogger logger,
            out bool scanComplete,
            out bool hasUncapturableDefaultHapticEndpoint)
        {
            scanComplete = true;
            hasUncapturableDefaultHapticEndpoint = false;
            var found = new List<HapticEndpointInfo>();
            var inventory = new List<string>();
            try
            {
                using (var enumerator = new MMDeviceEnumerator())
                {
                    var defaultId = TryGetDefaultRenderId(enumerator);
                    foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                    {
                        try
                        {
                            var isDefault = string.Equals(device.ID, defaultId, StringComparison.OrdinalIgnoreCase);
                            var verdict = Classify(device);
                            var haptic = verdict.IsHaptic;

                            // Logged whenever the set changes, naming what was seen and how it was
                            // judged. Without it a machine that still records haptics is
                            // indistinguishable from one that has no controller endpoint at all.
                            // A controller that is also the default output is the user's listening
                            // device, so its audio is kept and its haptics cannot be removed. Said
                            // in the inventory rather than its own line: the scan repeats, and a
                            // per-endpoint log line would repeat with it.
                            var keptAsOutput = haptic && isDefault;
                            hasUncapturableDefaultHapticEndpoint |= keptAsOutput;
                            inventory.Add(
                                $"'{Describe(device)}'{(isDefault ? " default" : string.Empty)}" +
                                $"{(haptic ? " HAPTIC" : string.Empty)}" +
                                $"{(keptAsOutput ? " kept-as-output" : string.Empty)} [{verdict.Identity}]");

                            if (!haptic || keptAsOutput)
                            {
                                continue;
                            }

                            found.Add(new HapticEndpointInfo { DeviceId = device.ID, Name = Describe(device) });
                        }
                        catch (Exception ex)
                        {
                            // One unreadable endpoint must not cost the scan the others.
                            scanComplete = false;
                            logger?.Debug(ex, "[Recording] A render endpoint could not be inspected.");
                        }
                        finally
                        {
                            try { device.Dispose(); } catch { }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                scanComplete = false;
                logger?.Warn(ex, "[Recording] Render endpoints could not be enumerated; no haptic reference will be captured.");
                return new List<HapticEndpointInfo>();
            }

            var line = string.Join(", ", inventory.ToArray());
            lock (InventoryGate)
            {
                if (!string.Equals(line, _lastInventory, StringComparison.Ordinal))
                {
                    _lastInventory = line;
                    logger?.Info("[Recording] Render endpoints: " + line);
                }
            }

            return found;
        }

        /// <summary>What an endpoint id was judged to be, and the identity it was judged by.</summary>
        private struct EndpointVerdict
        {
            public bool IsHaptic;
            public string Identity;
        }

        /// <summary>
        /// Classifies one endpoint, reusing the verdict for an id already seen. An id cannot change
        /// what hardware it belongs to, and the identity sweep reads the endpoint's whole property
        /// store — far too much work to repeat at rescan cadence.
        /// </summary>
        private static EndpointVerdict Classify(MMDevice device)
        {
            var id = device.ID ?? string.Empty;
            lock (InventoryGate)
            {
                if (Classified.TryGetValue(id, out var cached))
                {
                    return cached;
                }
            }

            var identities = IdentityCandidates(device);
            var verdict = new EndpointVerdict
            {
                Identity = FirstIdentity(identities),
                IsHaptic = HapticEndpointClassifier.IsHapticEndpoint(
                    identities,
                    TryRead(() => device.FriendlyName),
                    TryRead(() => device.DeviceFriendlyName)),
            };

            lock (InventoryGate)
            {
                Classified[id] = verdict;
            }

            return verdict;
        }

        /// <summary>The identity the classifier judged by, for the log; endpoints publish none at all.</summary>
        private static string FirstIdentity(IEnumerable<string> identities)
        {
            foreach (var identity in identities)
            {
                return identity;
            }

            return "no vendor id published";
        }

        /// <summary>
        /// Every string this endpoint publishes that carries a USB vendor id, which is where the
        /// pad's identity actually lives — as a device instance path, a hardware id, or a device
        /// interface path, depending on the driver. The whole property store is swept rather than a
        /// fixed set of keys: <see cref="MMDevice.InstanceId"/> alone reports "Unknown" on plenty of
        /// real endpoints, and which key is populated is the driver's choice, not a constant.
        /// </summary>
        private static IEnumerable<string> IdentityCandidates(MMDevice device)
        {
            var candidates = new List<string>();
            Collect(candidates, TryRead(() => device.InstanceId));
            try
            {
                var properties = device.Properties;
                for (var i = 0; i < properties.Count; i++)
                {
                    try
                    {
                        var value = properties.GetValue(i).Value;
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
                        // Property types this build cannot marshal are of no interest here.
                    }
                }
            }
            catch
            {
                // An endpoint with no readable property store still gets the name fallback.
            }

            return candidates;
        }

        /// <summary>Keeps the strings that carry a vendor id, in either the USB or Bluetooth form.</summary>
        private static void Collect(List<string> candidates, string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            if (value.IndexOf("VID_", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("VID&", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                candidates.Add(value);
            }
        }

        private static string Describe(MMDevice device)
        {
            return TryRead(() => device.FriendlyName) ?? TryRead(() => device.DeviceFriendlyName) ?? device.ID;
        }

        private static string TryGetDefaultRenderId(MMDeviceEnumerator enumerator)
        {
            try
            {
                using (var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console))
                {
                    return device?.ID;
                }
            }
            catch
            {
                // No default endpoint at all (every output disabled) is a valid state.
                return null;
            }
        }

        /// <summary>
        /// Reads one endpoint property. Each is a separate COM property-store call that a driver can
        /// fail independently, and a missing name must not disqualify an endpoint its instance id
        /// already identifies.
        /// </summary>
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
