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
                TryRead(() => device.InstanceId),
                TryRead(() => device.FriendlyName),
                TryRead(() => device.DeviceFriendlyName));
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
