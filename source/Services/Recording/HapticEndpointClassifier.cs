using System;
using System.Collections.Generic;
using System.Globalization;
using PlayniteAchievements.Services.UI;

namespace PlayniteAchievements.Services.Recording
{
    /// <summary>
    /// Decides whether a Windows render endpoint belongs to a game controller whose haptics are
    /// played as audio. A DualSense (and a DualShock 4) exposes a render endpoint over USB and the
    /// game renders its haptic waveform to it; process loopback captures every endpoint a process
    /// renders to, so that waveform lands in clip audio unless it is identified and removed.
    ///
    /// Pure string matching, so it unit-tests without an audio stack: the identity comes from the
    /// endpoint's device instance id, with the friendly name as a fallback for devices whose
    /// instance id carries no vendor/product pair (Bluetooth enumerations, virtual drivers).
    /// </summary>
    internal static class HapticEndpointClassifier
    {
        // Names Windows and the pad vendors give these endpoints. Matched case-insensitively as a
        // substring, because the endpoint name is usually a composite ("Speakers (Wireless
        // Controller)").
        private static readonly string[] HapticDeviceNames =
        {
            "wireless controller",
            "dualsense",
            "dualshock",
        };

        /// <summary>
        /// Whether this endpoint is a controller's own audio device. Any input may be null; a match
        /// on a hardware identity is preferred because a name can be renamed by the user.
        /// </summary>
        public static bool IsHapticEndpoint(string instanceId, string friendlyName, string deviceDescription)
        {
            return IsHapticEndpoint(new[] { instanceId }, friendlyName, deviceDescription);
        }

        /// <summary>
        /// The same decision over several candidate identity strings, because which property carries
        /// the underlying hardware id varies by driver: an endpoint may expose it as a device
        /// instance path, a hardware id, or a device interface path, and some expose none at all.
        /// <para>
        /// Either signal is enough. An endpoint can publish a vendor id that belongs to a hub, a
        /// dongle or a composite parent rather than the pad, so an id that is not a known pad must
        /// not veto a name that plainly says "Wireless Controller" — a pad the scan fails to
        /// recognise makes the whole feature dead, while a false positive only costs a device the
        /// user is not listening on (the default output is never offered here).
        /// </para>
        /// </summary>
        public static bool IsHapticEndpoint(
            IEnumerable<string> identityCandidates, string friendlyName, string deviceDescription)
        {
            if (identityCandidates != null)
            {
                foreach (var candidate in identityCandidates)
                {
                    if (TryParseVendorProduct(candidate, out var vendorId, out var productId) &&
                        ControllerPadIds.RendersHapticsAsAudio(vendorId, productId))
                    {
                        return true;
                    }
                }
            }

            return MatchesHapticName(friendlyName) || MatchesHapticName(deviceDescription);
        }

        private static bool MatchesHapticName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            foreach (var candidate in HapticDeviceNames)
            {
                if (name.IndexOf(candidate, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Reads the vendor/product pair out of a device instance id. Two forms occur:
        /// <c>USB\VID_054C&amp;PID_0CE6&amp;MI_03\...</c> for a USB endpoint, and the Bluetooth
        /// enumerator's <c>..._VID&amp;0002054C_PID&amp;0CE6\...</c>, where the vendor field carries a
        /// two-byte namespace prefix before the id itself.
        /// </summary>
        internal static bool TryParseVendorProduct(string instanceId, out int vendorId, out int productId)
        {
            vendorId = 0;
            productId = 0;
            if (string.IsNullOrEmpty(instanceId))
            {
                return false;
            }

            if (TryReadHexField(instanceId, "VID_", out vendorId) &&
                TryReadHexField(instanceId, "PID_", out productId))
            {
                return true;
            }

            return TryReadHexField(instanceId, "VID&", out vendorId) &&
                   TryReadHexField(instanceId, "PID&", out productId);
        }

        /// <summary>
        /// Reads the hexadecimal digits that follow <paramref name="marker"/>, keeping the last four
        /// so the Bluetooth form's namespace prefix (0002054c) resolves to the vendor id itself.
        /// </summary>
        private static bool TryReadHexField(string instanceId, string marker, out int value)
        {
            value = 0;
            var markerIndex = instanceId.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0)
            {
                return false;
            }

            var start = markerIndex + marker.Length;
            var end = start;
            while (end < instanceId.Length && Uri.IsHexDigit(instanceId[end]))
            {
                end++;
            }

            var digits = end - start;
            if (digits < 4)
            {
                return false;
            }

            return int.TryParse(
                instanceId.Substring(end - 4, 4),
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out value);
        }
    }
}
