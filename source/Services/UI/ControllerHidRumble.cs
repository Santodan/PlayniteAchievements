using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Playnite.SDK;

namespace PlayniteAchievements.Services.UI
{
    /// <summary>
    /// Rumbles controllers that expose no XInput device of their own: DualSense, DualShock 4, and
    /// the 2026 Valve Steam Controller. Each pad is driven by writing its vendor rumble output
    /// report over raw HID.
    ///
    /// Report layouts, magnitude scaling, and device IDs are transcribed from SDL's hidapi drivers
    /// (SDL_hidapi_ps5.c, SDL_hidapi_ps4.c, SDL_hidapi_steam_triton.c, controller_list.h).
    ///
    /// A set is opened per pulse and disposed with it, so no device state is cached between
    /// pulses and a hot-plugged pad is picked up by the next notification.
    /// </summary>
    internal sealed class ControllerHidRumble : IDisposable
    {
        private const int SteamControllerReportLength = 10;

        // A DualSense emulates rumble through its haptic actuators, which run stronger than the
        // motors in an Xbox or DualShock 4 pad, so SDL's compatibility path halves the magnitude.
        // Keeping that shift is what makes one strength slider feel the same across pads.
        private const int SonyStrengthShift = 1;

        private enum PadFamily
        {
            DualSense,
            DualShock4,
            SteamController,
        }

        private static readonly IReadOnlyDictionary<int, PadFamily> KnownPads = new Dictionary<int, PadFamily>
        {
            // Sony, vendor 0x054c.
            { Key(0x054c, 0x0ce6), PadFamily.DualSense },   // DualSense
            { Key(0x054c, 0x0df2), PadFamily.DualSense },   // DualSense Edge
            { Key(0x054c, 0x05c4), PadFamily.DualShock4 },  // DualShock 4
            { Key(0x054c, 0x09cc), PadFamily.DualShock4 },  // DualShock 4 slim
            { Key(0x054c, 0x0ba0), PadFamily.DualShock4 },  // DualShock 4 wireless dongle
            { Key(0x054c, 0x05c5), PadFamily.DualShock4 },  // DualShock 4 strikepad

            // Valve, vendor 0x28de. 0x1302/0x1303 are the 2026 controller itself (wired and BLE);
            // 0x1304/0x1305 are its Proteus and Nereid dongles.
            { Key(0x28de, 0x1302), PadFamily.SteamController },
            { Key(0x28de, 0x1303), PadFamily.SteamController },
            { Key(0x28de, 0x1304), PadFamily.SteamController },
            { Key(0x28de, 0x1305), PadFamily.SteamController },
        };

        // Windows rejects a write whose length is not the device's OutputReportByteLength, so every
        // report is padded out to it. For the Sony pads that length also selects the layout, since
        // their reports fill it exactly: a DualSense reports 48 over USB and 78 over Bluetooth, a
        // DualShock 4 32 and 78. The Steam Controller's message is 10 bytes regardless and is sent
        // inside a larger report, so its layout does not depend on the length at all.
        private static readonly IReadOnlyDictionary<PadFamily, int[]> SonyReportLengths = new Dictionary<PadFamily, int[]>
        {
            { PadFamily.DualSense, new[] { 48, 78 } },
            { PadFamily.DualShock4, new[] { 32, 78 } },
        };

        private static volatile bool _hidUnavailable;
        private static string _lastInventory;

        private readonly List<Pad> _pads;
        private readonly ILogger _logger;

        private ControllerHidRumble(List<Pad> pads, ILogger logger)
        {
            _pads = pads ?? new List<Pad>();
            _logger = logger;
        }

        /// <summary>
        /// True when a matched pad's rumble command expires on its own and has to be renewed for
        /// the length of the pulse. Only the Steam Controller behaves this way.
        /// </summary>
        public bool NeedsResend { get; private set; }

        /// <summary>
        /// Enumerates and opens every supported pad. Never throws and never returns null; a
        /// missing HID stack or a system with no supported pad yields an empty set.
        /// </summary>
        public static ControllerHidRumble Open(ILogger logger)
        {
            var pads = new List<Pad>();
            if (_hidUnavailable)
            {
                return new ControllerHidRumble(pads, logger);
            }

            var unmatched = new List<string>();
            try
            {
                foreach (var path in EnumerateHidPaths())
                {
                    // Both HID path formats embed the vendor hex ("&vid_054c&" for USB,
                    // "_vid&0002054c_" for Bluetooth), so this prefilter avoids opening every
                    // keyboard and mouse on the system. Identity is still confirmed below.
                    if (path.IndexOf("054c", StringComparison.OrdinalIgnoreCase) < 0 &&
                        path.IndexOf("28de", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    var pad = TryOpenPad(path, unmatched);
                    if (pad != null)
                    {
                        pads.Add(pad);
                    }
                }
            }
            catch (DllNotFoundException ex)
            {
                _hidUnavailable = true;
                logger?.Debug(ex, "HID is unavailable; only XInput controllers will vibrate this session.");
            }
            catch (EntryPointNotFoundException ex)
            {
                _hidUnavailable = true;
                logger?.Debug(ex, "HID is unavailable; only XInput controllers will vibrate this session.");
            }
            catch (Exception ex)
            {
                logger?.Debug(ex, "Enumerating HID controllers for vibration failed.");
            }

            var set = new ControllerHidRumble(pads, logger);
            foreach (var pad in pads)
            {
                if (pad.Family == PadFamily.SteamController)
                {
                    set.NeedsResend = true;
                    break;
                }
            }

            LogInventory(pads, unmatched, logger);
            return set;
        }

        /// <summary>
        /// Writes the rumble report to every open pad at <paramref name="speed"/> (0 stops).
        /// A pad that rejects the write is dropped from the set.
        /// </summary>
        public void Set(ushort speed)
        {
            for (var i = _pads.Count - 1; i >= 0; i--)
            {
                var pad = _pads[i];
                var report = BuildReport(pad.Family, pad.ReportLength, speed);
                var error = 0;
                if (report != null && Write(pad.Handle, report, out error))
                {
                    if (speed == 0 && pad.Family == PadFamily.DualSense)
                    {
                        var release = BuildDualSenseRelease(pad.ReportLength);
                        if (release != null)
                        {
                            Write(pad.Handle, release, out error);
                        }
                    }

                    continue;
                }

                _logger?.Debug(string.Format(
                    CultureInfo.InvariantCulture,
                    "HID vibration write failed for {0} (win32={1}); dropping it for this pulse.",
                    pad.Label,
                    error));
                pad.Dispose();
                _pads.RemoveAt(i);
            }
        }

        public void Dispose()
        {
            foreach (var pad in _pads)
            {
                pad.Dispose();
            }

            _pads.Clear();
        }

        private static Pad TryOpenPad(string path, List<string> unmatched)
        {
            SafeFileHandle handle = null;
            try
            {
                handle = NativeMethods.CreateFileW(
                    path,
                    NativeMethods.GENERIC_WRITE,
                    NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
                    IntPtr.Zero,
                    NativeMethods.OPEN_EXISTING,
                    0,
                    IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    return null;
                }

                var attributes = new NativeMethods.HiddAttributes
                {
                    Size = (uint)Marshal.SizeOf(typeof(NativeMethods.HiddAttributes)),
                };
                if (!NativeMethods.HidD_GetAttributes(handle, ref attributes))
                {
                    return null;
                }

                var isKnownVendor = attributes.VendorId == 0x054c || attributes.VendorId == 0x28de;
                if (!KnownPads.TryGetValue(Key(attributes.VendorId, attributes.ProductId), out var family))
                {
                    if (isKnownVendor)
                    {
                        unmatched.Add(DescribeUnmatched(handle, attributes));
                    }

                    return null;
                }

                // SDL only treats interfaces 2 through 5 of the Valve dongles as controllers.
                if ((attributes.ProductId == 0x1304 || attributes.ProductId == 0x1305) &&
                    !IsDongleControllerInterface(path))
                {
                    return null;
                }

                // The write must be exactly this long, and it is also what tells a Sony pad's USB
                // layout from its Bluetooth one.
                var reportLength = ReadOutputReportLength(handle);
                if (reportLength <= 0 || !IsUsableReportLength(family, reportLength))
                {
                    unmatched.Add(DescribeUnmatched(handle, attributes));
                    return null;
                }

                if (!Write(handle, BuildNeutralReport(family, reportLength), out var probeError))
                {
                    unmatched.Add(string.Format(
                        CultureInfo.InvariantCulture,
                        "{0} out={1} (write rejected, win32={2})",
                        Describe(attributes.VendorId, attributes.ProductId),
                        reportLength,
                        probeError));
                    return null;
                }

                var pad = new Pad
                {
                    Handle = handle,
                    Family = family,
                    ReportLength = reportLength,
                    Label = string.Format(
                        CultureInfo.InvariantCulture,
                        "{0} {1} out={2}",
                        family,
                        Describe(attributes.VendorId, attributes.ProductId),
                        reportLength),
                };
                handle = null;
                return pad;
            }
            catch (Exception ex) when (!(ex is DllNotFoundException) && !(ex is EntryPointNotFoundException))
            {
                // One uncooperative device must not stop the rest of the scan; a missing HID stack
                // is a different problem and is left to the caller's latch.
                return null;
            }
            finally
            {
                handle?.Dispose();
            }
        }

        /// <summary>
        /// Reads the device's output report length, which every write must match exactly.
        /// Returns 0 when the device reports no output pipe or the caps cannot be read.
        /// </summary>
        private static int ReadOutputReportLength(SafeFileHandle handle)
        {
            var preparsed = IntPtr.Zero;
            try
            {
                if (!NativeMethods.HidD_GetPreparsedData(handle, out preparsed) || preparsed == IntPtr.Zero)
                {
                    return 0;
                }

                var caps = default(NativeMethods.HidpCaps);
                if (NativeMethods.HidP_GetCaps(preparsed, ref caps) != NativeMethods.HIDP_STATUS_SUCCESS)
                {
                    return 0;
                }

                return caps.OutputReportByteLength;
            }
            finally
            {
                if (preparsed != IntPtr.Zero)
                {
                    NativeMethods.HidD_FreePreparsedData(preparsed);
                }
            }
        }

        /// <summary>
        /// A Sony pad must report one of its two known report lengths, since that is what selects
        /// the USB or Bluetooth layout. A Steam Controller's message is padded into whatever the
        /// interface offers, so any length that holds the message will do.
        /// </summary>
        private static bool IsUsableReportLength(PadFamily family, int reportLength)
        {
            if (SonyReportLengths.TryGetValue(family, out var lengths))
            {
                return Array.IndexOf(lengths, reportLength) >= 0;
            }

            return reportLength >= SteamControllerReportLength;
        }

        /// <summary>
        /// A report that changes nothing, used to confirm the interface really accepts writes
        /// before it is added to the set.
        /// </summary>
        private static byte[] BuildNeutralReport(PadFamily family, int reportLength)
        {
            // A DualSense uses its release report so that merely checking for writability does not
            // switch the pad out of audio haptics.
            return family == PadFamily.DualSense
                ? BuildDualSenseRelease(reportLength)
                : BuildReport(family, reportLength, 0);
        }

        private static string DescribeUnmatched(SafeFileHandle handle, NativeMethods.HiddAttributes attributes)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0} out={1}",
                Describe(attributes.VendorId, attributes.ProductId),
                ReadOutputReportLength(handle));
        }

        private static bool Write(SafeFileHandle handle, byte[] report, out int error)
        {
            if (!NativeMethods.WriteFile(handle, report, (uint)report.Length, out var written, IntPtr.Zero))
            {
                error = Marshal.GetLastWin32Error();
                return false;
            }

            // A short write leaves no win32 error to report, so flag it distinctly.
            error = written == report.Length ? 0 : -1;
            return error == 0;
        }

        private static byte[] BuildReport(PadFamily family, int reportLength, ushort speed)
        {
            switch (family)
            {
                case PadFamily.DualSense:
                    return BuildDualSense(reportLength, speed);
                case PadFamily.DualShock4:
                    return BuildDualShock4(reportLength, speed);
                case PadFamily.SteamController:
                    return BuildSteamController(reportLength, speed);
                default:
                    return null;
            }
        }

        /// <summary>
        /// DualSense effects report: 0x02 over USB (48 bytes, effects at offset 1) and 0x31 over
        /// Bluetooth (78 bytes, effects at offset 3 behind a tag and magic byte, CRC trailer).
        /// See <see cref="BuildDualSenseRelease"/> for the report that ends rumble emulation.
        /// </summary>
        private static byte[] BuildDualSense(int reportLength, ushort speed)
        {
            var report = new byte[reportLength];
            int offset;
            if (reportLength == 48)
            {
                report[0] = 0x02;
                offset = 1;
            }
            else if (reportLength == 78)
            {
                report[0] = 0x31;
                report[1] = 0x00; // Tag and sequence.
                report[2] = 0x10; // Magic value.
                offset = 3;
            }
            else
            {
                return null;
            }

            // The enable bits are always set, including for a stop: a zero magnitude with rumble
            // emulation still enabled is what actually halts the motors. The follow-up release
            // report clears the bits so audio haptics come back.
            var magnitude = (byte)((speed >> 8) >> SonyStrengthShift);
            report[offset] = 0x01 | 0x02;      // Enable rumble emulation, disable audio haptics.
            report[offset + 2] = magnitude;    // ucRumbleRight
            report[offset + 3] = magnitude;    // ucRumbleLeft

            AppendBluetoothCrc(report, reportLength == 78);
            return report;
        }

        /// <summary>
        /// DualSense report with every enable bit clear. SDL relies on this to restore the pad's
        /// audio haptics once rumble emulation is no longer wanted.
        /// </summary>
        private static byte[] BuildDualSenseRelease(int reportLength)
        {
            var report = new byte[reportLength];
            if (reportLength == 48)
            {
                report[0] = 0x02;
            }
            else if (reportLength == 78)
            {
                report[0] = 0x31;
                report[1] = 0x00;
                report[2] = 0x10;
            }
            else
            {
                return null;
            }

            AppendBluetoothCrc(report, reportLength == 78);
            return report;
        }

        /// <summary>
        /// DualShock 4 effects report: 0x05 over USB (32 bytes, effects at offset 4) and 0x11 over
        /// Bluetooth (78 bytes, effects at offset 6, CRC trailer). The effect mask selects rumble
        /// only, so the lightbar keeps whatever colour it already has.
        /// </summary>
        private static byte[] BuildDualShock4(int reportLength, ushort speed)
        {
            const byte EffectRumble = 0x01;

            var report = new byte[reportLength];
            int offset;
            if (reportLength == 32)
            {
                report[0] = 0x05;
                report[1] = EffectRumble;
                offset = 4;
            }
            else if (reportLength == 78)
            {
                report[0] = 0x11;
                report[1] = 0xC0 | 4; // HID + CRC magic, and a 4 ms report interval.
                report[3] = EffectRumble;
                offset = 6;
            }
            else
            {
                return null;
            }

            var magnitude = (byte)(speed >> 8);
            report[offset] = magnitude;     // ucRumbleRight
            report[offset + 1] = magnitude; // ucRumbleLeft

            AppendBluetoothCrc(report, reportLength == 78);
            return report;
        }

        /// <summary>
        /// Steam Controller haptic rumble output report (ID_OUT_REPORT_HAPTIC_RUMBLE): a packed
        /// 10-byte message of type, intensity, and a speed/gain pair per side. This command expires
        /// on its own, which is why <see cref="NeedsResend"/> exists.
        ///
        /// The message is shorter than the interface's output report, so it is zero-padded out to
        /// <paramref name="reportLength"/> — the same thing hidapi does on Windows before writing,
        /// and what SDL relies on when it hands hid_write only the 10 message bytes.
        /// </summary>
        private static byte[] BuildSteamController(int reportLength, ushort speed)
        {
            if (reportLength < SteamControllerReportLength)
            {
                return null;
            }

            var report = new byte[reportLength];
            report[0] = 0x80;                          // ID_OUT_REPORT_HAPTIC_RUMBLE
            report[1] = 0x00;                          // type
            report[2] = 0x00;                          // intensity, low byte
            report[3] = 0x00;                          // intensity, high byte
            report[4] = (byte)(speed & 0xFF);          // left.speed
            report[5] = (byte)(speed >> 8);
            report[6] = 0x00;                          // left.gain
            report[7] = (byte)(speed & 0xFF);          // right.speed
            report[8] = (byte)(speed >> 8);
            report[9] = 0x00;                          // right.gain
            return report;
        }

        /// <summary>
        /// Sony Bluetooth reports carry a CRC-32 of the 0xA2 hidp header byte followed by the
        /// report body, stored little-endian in the trailing four bytes.
        /// </summary>
        private static void AppendBluetoothCrc(byte[] report, bool isBluetooth)
        {
            if (!isBluetooth)
            {
                return;
            }

            var crc = Crc32(0xA2, report, report.Length - 4);
            report[report.Length - 4] = (byte)(crc & 0xFF);
            report[report.Length - 3] = (byte)((crc >> 8) & 0xFF);
            report[report.Length - 2] = (byte)((crc >> 16) & 0xFF);
            report[report.Length - 1] = (byte)((crc >> 24) & 0xFF);
        }

        // Reflected IEEE CRC-32 (polynomial 0xEDB88320), computed bitwise to keep the helper small.
        private static uint Crc32(byte prefix, byte[] data, int length)
        {
            var crc = 0xFFFFFFFFu;
            crc = Crc32Byte(crc, prefix);
            for (var i = 0; i < length; i++)
            {
                crc = Crc32Byte(crc, data[i]);
            }

            return ~crc;
        }

        private static uint Crc32Byte(uint crc, byte value)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }

            return crc;
        }

        private static bool IsDongleControllerInterface(string path)
        {
            var marker = path.IndexOf("&mi_", StringComparison.OrdinalIgnoreCase);
            if (marker < 0)
            {
                return true;
            }

            var digits = path.Substring(marker + 4);
            if (digits.Length < 2 ||
                !int.TryParse(digits.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var interfaceNumber))
            {
                return true;
            }

            return interfaceNumber >= 2 && interfaceNumber <= 5;
        }

        private static IEnumerable<string> EnumerateHidPaths()
        {
            var buffer = ReadHidInterfaceList();
            if (buffer == null)
            {
                yield break;
            }

            // The buffer is a multi-sz block: null-separated paths ending in an empty string.
            var current = new StringBuilder();
            foreach (var c in buffer)
            {
                if (c != '\0')
                {
                    current.Append(c);
                    continue;
                }

                if (current.Length == 0)
                {
                    yield break;
                }

                yield return current.ToString();
                current.Clear();
            }
        }

        /// <summary>
        /// Fetches the present HID interface paths as a multi-sz block, retrying when a device
        /// arrives between sizing the buffer and filling it (CR_BUFFER_SMALL).
        /// </summary>
        private static char[] ReadHidInterfaceList()
        {
            const int CR_SUCCESS = 0;
            const int CR_BUFFER_SMALL = 0x1A;

            var hidGuid = NativeMethods.GuidDevInterfaceHid;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if (NativeMethods.CM_Get_Device_Interface_List_SizeW(
                        out var length,
                        ref hidGuid,
                        null,
                        NativeMethods.CM_GET_DEVICE_INTERFACE_LIST_PRESENT) != CR_SUCCESS || length < 2)
                {
                    return null;
                }

                var buffer = new char[length];
                var result = NativeMethods.CM_Get_Device_Interface_ListW(
                    ref hidGuid,
                    null,
                    buffer,
                    length,
                    NativeMethods.CM_GET_DEVICE_INTERFACE_LIST_PRESENT);
                if (result == CR_SUCCESS)
                {
                    return buffer;
                }

                if (result != CR_BUFFER_SMALL)
                {
                    return null;
                }
            }

            return null;
        }

        private static void LogInventory(List<Pad> pads, List<string> unmatched, ILogger logger)
        {
            if (logger == null)
            {
                return;
            }

            var summary = new StringBuilder();
            for (var i = 0; i < pads.Count; i++)
            {
                summary.Append(i == 0 ? string.Empty : "; ").Append(pads[i].Label);
            }

            if (pads.Count == 0)
            {
                summary.Append("none");
            }

            if (unmatched.Count > 0)
            {
                summary.Append("; unrecognized: ").Append(string.Join(", ", unmatched));
            }

            var inventory = summary.ToString();
            if (string.Equals(inventory, _lastInventory, StringComparison.Ordinal))
            {
                return;
            }

            _lastInventory = inventory;
            logger.Info("HID vibration controllers: " + inventory);
        }

        private static string Describe(ushort vendorId, ushort productId)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:x4}:{1:x4}", vendorId, productId);
        }

        private static int Key(int vendorId, int productId)
        {
            return (vendorId << 16) | productId;
        }

        private sealed class Pad : IDisposable
        {
            public SafeFileHandle Handle;
            public PadFamily Family;
            public int ReportLength;
            public string Label;

            public void Dispose()
            {
                Handle?.Dispose();
            }
        }

        private static class NativeMethods
        {
            public const uint GENERIC_WRITE = 0x40000000;
            public const uint FILE_SHARE_READ = 0x00000001;
            public const uint FILE_SHARE_WRITE = 0x00000002;
            public const uint OPEN_EXISTING = 3;
            public const uint CM_GET_DEVICE_INTERFACE_LIST_PRESENT = 0x00000001;
            public const int HIDP_STATUS_SUCCESS = 0x00110000;

            public static Guid GuidDevInterfaceHid = new Guid("4D1E55B2-F16F-11CF-88CB-001111000030");

            [StructLayout(LayoutKind.Sequential)]
            public struct HiddAttributes
            {
                public uint Size;
                public ushort VendorId;
                public ushort ProductId;
                public ushort VersionNumber;
            }

            /// <summary>
            /// HIDP_CAPS. Only the report lengths are needed, so the trailing 17 USHORT reserved
            /// words and the link-collection counts are left as padding via an explicit size.
            /// </summary>
            [StructLayout(LayoutKind.Sequential, Size = 64)]
            public struct HidpCaps
            {
                public ushort Usage;
                public ushort UsagePage;
                public ushort InputReportByteLength;
                public ushort OutputReportByteLength;
                public ushort FeatureReportByteLength;
            }

            [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
            public static extern int CM_Get_Device_Interface_List_SizeW(
                out uint pulLen,
                ref Guid interfaceClassGuid,
                string deviceID,
                uint ulFlags);

            [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
            public static extern int CM_Get_Device_Interface_ListW(
                ref Guid interfaceClassGuid,
                string deviceID,
                char[] buffer,
                uint bufferLen,
                uint ulFlags);

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            public static extern SafeFileHandle CreateFileW(
                string fileName,
                uint desiredAccess,
                uint shareMode,
                IntPtr securityAttributes,
                uint creationDisposition,
                uint flagsAndAttributes,
                IntPtr templateFile);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool WriteFile(
                SafeFileHandle handle,
                byte[] buffer,
                uint numberOfBytesToWrite,
                out uint numberOfBytesWritten,
                IntPtr overlapped);

            [DllImport("hid.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool HidD_GetAttributes(SafeFileHandle handle, ref HiddAttributes attributes);

            [DllImport("hid.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr preparsedData);

            [DllImport("hid.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

            [DllImport("hid.dll")]
            public static extern int HidP_GetCaps(IntPtr preparsedData, ref HidpCaps capabilities);
        }
    }
}
