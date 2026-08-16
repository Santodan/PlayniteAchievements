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
        public static IReadOnlyList<HapticEndpointInfo> FindHapticEndpoints(ILogger logger)
        {
            var found = new List<HapticEndpointInfo>();
            try
            {
                using (var enumerator = new MMDeviceEnumerator())
                {
                    var defaultId = TryGetDefaultRenderId(enumerator);
                    foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                    {
                        try
                        {
                            if (!IsHaptic(device))
                            {
                                continue;
                            }

                            if (string.Equals(device.ID, defaultId, StringComparison.OrdinalIgnoreCase))
                            {
                                logger?.Info(
                                    $"[Recording] '{Describe(device)}' is a controller audio device but also the " +
                                    "default output, so its audio is kept: haptics cannot be removed from this session's clips.");
                                continue;
                            }

                            found.Add(new HapticEndpointInfo { DeviceId = device.ID, Name = Describe(device) });
                        }
                        catch (Exception ex)
                        {
                            // One unreadable endpoint must not cost the scan the others.
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
                logger?.Debug(ex, "[Recording] Render endpoints could not be enumerated; no haptic reference will be captured.");
                return new List<HapticEndpointInfo>();
            }

            return found;
        }

        private static bool IsHaptic(MMDevice device)
        {
            return HapticEndpointClassifier.IsHapticEndpoint(
                IdentityCandidates(device),
                TryRead(() => device.FriendlyName),
                TryRead(() => device.DeviceFriendlyName));
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
