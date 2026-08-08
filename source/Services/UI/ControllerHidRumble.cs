using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
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

        /// <summary>
        /// Temporary: when true the settings Test button sweeps candidate report variants instead of
        /// firing a normal pulse, so the variant real hardware responds to can be identified. Set
        /// back to false once the answer is folded into the builders.
        /// </summary>
        internal static readonly bool DiagnosticVariantSweep = true;

        /// <summary>
        /// Only the DualShock 4 still has an open question: whether the required high nibble works
        /// without also claiming the lightbar. Every other family is settled.
        /// </summary>
        private static int VariantCount(PadFamily family)
        {
            return family == PadFamily.DualShock4 ? 2 : 1;
        }

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

        // Two different lengths matter, and conflating them is why an earlier attempt failed.
        //
        // The transmit length is whatever OutputReportByteLength says, and Windows rejects any write
        // that is not exactly that long. It is the MAXIMUM across every output report the descriptor
        // declares, not the size of the one being sent: a Bluetooth DualSense reports 547.
        //
        // The logical length is the rumble report's own size, which fixes the field offsets and
        // where the Bluetooth CRC goes. A report is built at its logical length and then zero-padded
        // out to the transmit length, which is exactly what hidapi does on Windows.
        private const int SonyBluetoothLogicalLength = 78;

        private static readonly IReadOnlyDictionary<PadFamily, int> SonyUsbLogicalLengths = new Dictionary<PadFamily, int>
        {
            { PadFamily.DualSense, 48 },
            { PadFamily.DualShock4, 32 },
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
                var report = PadToTransmitLength(
                    BuildReport(pad.Family, pad.LogicalLength, speed),
                    pad.TransmitLength);
                var error = 0;
                if (report != null && Write(pad.Handle, report, out error))
                {
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

        /// <summary>
        /// Temporary hardware-identification aid. Tries each candidate report variant in turn, and
        /// makes each one self-identifying by buzzing it a countable number of times: variant 0
        /// buzzes once, variant 1 twice, variant 2 three times, variant 3 four times. Groups are
        /// separated by a long pause, so whichever variant the pad responds to can be named by
        /// counting the pulses felt, with no need to match log timestamps.
        ///
        /// Remove once the working variant is known and folded into the builders as variant 0.
        /// </summary>
        public static void RunDiagnosticSweep(int strengthPercent, ILogger logger)
        {
            const int BuzzMs = 220;
            const int WithinGroupGapMs = 170;
            const int BetweenGroupsMs = 1500;
            const int BetweenPadsMs = 2500;

            var clamped = Math.Max(1, Math.Min(100, strengthPercent));
            var speed = (ushort)Math.Round(clamped / 100.0 * ushort.MaxValue);
            Task.Run(async () =>
            {
                try
                {
                    using (var set = Open(logger))
                    {
                        if (set._pads.Count == 0)
                        {
                            logger?.Info("Vibration variant sweep: no HID pads found to test.");
                            return;
                        }

                        for (var padIndex = 0; padIndex < set._pads.Count; padIndex++)
                        {
                            var pad = set._pads[padIndex];
                            if (padIndex > 0)
                            {
                                await Task.Delay(BetweenPadsMs).ConfigureAwait(false);
                            }

                            var variants = VariantCount(pad.Family);
                            logger?.Info(string.Format(
                                CultureInfo.InvariantCulture,
                                "Vibration variant sweep: {0} — {1} variants, each buzzing (variant + 1) times.",
                                pad.Label,
                                variants));

                            for (var variant = 0; variant < variants; variant++)
                            {
                                if (variant > 0)
                                {
                                    await Task.Delay(BetweenGroupsMs).ConfigureAwait(false);
                                }

                                var on = PadToTransmitLength(
                                    BuildReport(pad.Family, pad.LogicalLength, speed, variant),
                                    pad.TransmitLength);
                                var off = PadToTransmitLength(
                                    BuildReport(pad.Family, pad.LogicalLength, 0, variant),
                                    pad.TransmitLength);
                                if (on == null || off == null)
                                {
                                    continue;
                                }

                                logger?.Info(string.Format(
                                    CultureInfo.InvariantCulture,
                                    "Vibration variant sweep: variant {0} now — expect {1} buzz(es).",
                                    variant,
                                    variant + 1));

                                var error = 0;
                                var wrote = true;
                                for (var buzz = 0; buzz <= variant && wrote; buzz++)
                                {
                                    if (buzz > 0)
                                    {
                                        await Task.Delay(WithinGroupGapMs).ConfigureAwait(false);
                                    }

                                    wrote = Write(pad.Handle, on, out error);
                                    await Task.Delay(BuzzMs).ConfigureAwait(false);
                                    Write(pad.Handle, off, out _);
                                }

                                if (!wrote)
                                {
                                    logger?.Info(string.Format(
                                        CultureInfo.InvariantCulture,
                                        "Vibration variant sweep: variant {0} write failed (win32={1}).",
                                        variant,
                                        error));
                                }
                            }
                        }

                        logger?.Info("Vibration variant sweep: finished. Report how many buzzes you felt.");
                    }
                }
                catch (Exception ex)
                {
                    logger?.Debug(ex, "Vibration variant sweep failed.");
                }
            });
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

                ReadReportLengths(handle, out var transmitLength, out var featureLength);
                var logicalLength = transmitLength <= 0 ? 0 : ResolveLogicalLength(family, transmitLength);
                if (logicalLength == 0)
                {
                    unmatched.Add(DescribeUnmatched(handle, attributes));
                    return null;
                }

                if (logicalLength == SonyBluetoothLogicalLength)
                {
                    EnableEnhancedReports(handle, family, featureLength);
                }

                if (!Write(handle, BuildNeutralReport(family, logicalLength, transmitLength), out var probeError))
                {
                    unmatched.Add(string.Format(
                        CultureInfo.InvariantCulture,
                        "{0} out={1} (write rejected, win32={2})",
                        Describe(attributes.VendorId, attributes.ProductId),
                        transmitLength,
                        probeError));
                    return null;
                }

                var pad = new Pad
                {
                    Handle = handle,
                    Family = family,
                    LogicalLength = logicalLength,
                    TransmitLength = transmitLength,
                    Label = string.Format(
                        CultureInfo.InvariantCulture,
                        "{0} {1} report={2} out={3}",
                        family,
                        Describe(attributes.VendorId, attributes.ProductId),
                        logicalLength,
                        transmitLength),
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
            ReadReportLengths(handle, out var outputLength, out _);
            return outputLength;
        }

        private static void ReadReportLengths(SafeFileHandle handle, out int outputLength, out int featureLength)
        {
            outputLength = 0;
            featureLength = 0;

            var preparsed = IntPtr.Zero;
            try
            {
                if (!NativeMethods.HidD_GetPreparsedData(handle, out preparsed) || preparsed == IntPtr.Zero)
                {
                    return;
                }

                var caps = default(NativeMethods.HidpCaps);
                if (NativeMethods.HidP_GetCaps(preparsed, ref caps) != NativeMethods.HIDP_STATUS_SUCCESS)
                {
                    return;
                }

                outputLength = caps.OutputReportByteLength;
                featureLength = caps.FeatureReportByteLength;
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
        /// Over Bluetooth a Sony pad starts in its simple DirectInput report mode and ignores the
        /// full effects report. Reading one feature report is what switches it into enhanced mode:
        /// SDL notes that reading the DualSense serial number "will also enable enhanced reports
        /// over Bluetooth", and a DualShock 4 is switched the same way by reading report 0x02.
        /// Best effort — a failure is left to the writability check that follows.
        /// </summary>
        private static void EnableEnhancedReports(SafeFileHandle handle, PadFamily family, int featureLength)
        {
            if (featureLength <= 1)
            {
                return;
            }

            byte[] reportIds;
            switch (family)
            {
                case PadFamily.DualSense:
                    // SDL reads both of these and notes each one enables enhanced reports.
                    reportIds = new byte[] { 0x09, 0x20 }; // Serial number, firmware info.
                    break;
                case PadFamily.DualShock4:
                    reportIds = new byte[] { 0x02 }; // Calibration, the classic full-mode trigger.
                    break;
                default:
                    return;
            }

            foreach (var reportId in reportIds)
            {
                var buffer = new byte[featureLength];
                buffer[0] = reportId;
                NativeMethods.HidD_GetFeature(handle, buffer, (uint)buffer.Length);
            }
        }

        /// <summary>
        /// Picks the rumble report's own size for a device whose writes must be
        /// <paramref name="transmitLength"/> bytes long. Returns 0 when the interface cannot carry
        /// the report.
        ///
        /// A Sony pad's USB descriptor declares the effects report as its largest output report, so
        /// a transmit length equal to that size means USB; anything larger (547 on Bluetooth) means
        /// the Bluetooth layout. A Steam Controller message is always the same size.
        /// </summary>
        private static int ResolveLogicalLength(PadFamily family, int transmitLength)
        {
            if (SonyUsbLogicalLengths.TryGetValue(family, out var usbLength))
            {
                if (transmitLength == usbLength)
                {
                    return usbLength;
                }

                return transmitLength >= SonyBluetoothLogicalLength ? SonyBluetoothLogicalLength : 0;
            }

            return transmitLength >= SteamControllerReportLength ? SteamControllerReportLength : 0;
        }

        /// <summary>
        /// Zero-pads a report out to the length the device demands for every write.
        /// </summary>
        private static byte[] PadToTransmitLength(byte[] report, int transmitLength)
        {
            if (report == null || report.Length > transmitLength)
            {
                return null;
            }

            if (report.Length == transmitLength)
            {
                return report;
            }

            var padded = new byte[transmitLength];
            Buffer.BlockCopy(report, 0, padded, 0, report.Length);
            return padded;
        }

        /// <summary>
        /// A zero-magnitude report, used to confirm the interface really accepts writes before it is
        /// added to the set. Silent on every family, so probing has no audible or visible effect.
        /// </summary>
        private static byte[] BuildNeutralReport(PadFamily family, int logicalLength, int transmitLength)
        {
            return PadToTransmitLength(BuildReport(family, logicalLength, 0), transmitLength);
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

        /// <summary>
        /// Builds the rumble report at its own logical length. The caller pads the result out to the
        /// length the device demands for a write.
        /// </summary>
        private static byte[] BuildReport(PadFamily family, int logicalLength, ushort speed, int variant = 0)
        {
            switch (family)
            {
                case PadFamily.DualSense:
                    return BuildDualSense(logicalLength, speed, variant);
                case PadFamily.DualShock4:
                    return BuildDualShock4(logicalLength, speed, variant);
                case PadFamily.SteamController:
                    return BuildSteamController(logicalLength, speed);
                default:
                    return null;
            }
        }

        /// <summary>
        /// DualSense effects report: 0x02 over USB (48 bytes, effects at offset 1) and 0x31 over
        /// Bluetooth (78 bytes, effects at offset 3 behind a tag and magic byte, CRC trailer).
        /// See <see cref="BuildDualSenseRelease"/> for the report that ends rumble emulation.
        /// </summary>
        private static byte[] BuildDualSense(int reportLength, ushort speed, int variant)
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

            // A DualSense has two rumble paths and SDL chooses between them by reading the firmware
            // version out of a feature report: emulation via ucEnableBits1 bit 0 with the magnitude
            // halved on older firmware, or improved rumble via ucEnableBits3 bit 2 at full magnitude
            // on 2.24 and newer. Rather than probe the firmware, both enable bits are set — each
            // path reads the same two magnitude bytes, so whichever one the pad implements drives
            // the motors and the other is ignored. This is what makes a wireless adapter work, since
            // an adapter need not implement the same path as a directly connected pad.
            //
            // Confirmed on a DualSense, a DualSense Edge and a DualShock 4: additionally setting the
            // audio-haptics-disable bit (0x02), as SDL does, silences the motors instead of driving
            // them, so it is left alone. That also means audio haptics are never disturbed and no
            // follow-up release report is needed.
            //
            // The bits stay set for a stop as well: a zero magnitude with the paths still enabled is
            // what halts the motors.
            report[offset] = 0x01;                        // ucEnableBits1: rumble emulation.
            report[offset + 38] = 0x04;                   // ucEnableBits3: improved rumble.
            var magnitude = (byte)(speed >> 8);
            report[offset + 2] = magnitude;               // ucRumbleRight
            report[offset + 3] = magnitude;               // ucRumbleLeft

            AppendBluetoothCrc(report, reportLength == 78);
            return report;
        }

        /// <summary>
        /// DualShock 4 effects report: 0x05 over USB (32 bytes, effects at offset 4) and 0x11 over
        /// Bluetooth (78 bytes, effects at offset 6, CRC trailer). The effect mask selects rumble
        /// only, so the lightbar keeps whatever colour it already has.
        /// </summary>
        private static byte[] BuildDualShock4(int reportLength, ushort speed, int variant)
        {
            const byte EffectRumble = 0x01;

            var report = new byte[reportLength];
            int offset;
            if (reportLength == 32)
            {
                report[0] = 0x05;
                offset = 4;

                // Byte 1 is valid_flag0: bit 0 motor, bit 1 lightbar, bit 2 lightbar blink.
                // Testing showed 0xF3 works while 0x03 does not, so the high nibble is required and
                // SDL's rumble-only 0x01 is not enough. 0xF3 also sets the lightbar bit, which
                // obliges us to send a colour and would recolour the pad on every notification.
                //
                // Variant 0 asks whether the high nibble alone is sufficient (motor bit, no lightbar
                // bit, light untouched). Variant 1 is the known-good 0xF3, which sets the light
                // green. One buzz means we can leave the lightbar alone; two means we cannot.
                switch (variant)
                {
                    case 1:
                        report[1] = 0xF3; // Known good; sets the lightbar.
                        SetDualShock4Lightbar(report, offset, 0x00, 0xFF, 0x00);
                        break;
                    default:
                        report[1] = 0xF0 | EffectRumble; // High nibble, motor only, light untouched.
                        break;
                }
            }
            else if (reportLength == 78)
            {
                report[0] = 0x11;
                report[1] = 0xC0 | 4; // HID + CRC magic, and a 4 ms report interval.
                offset = 6;

                // Same question as USB, but valid_flag0 sits at byte 3 over Bluetooth.
                switch (variant)
                {
                    case 1:
                        report[2] = 0x20;
                        report[3] = 0xF3;
                        SetDualShock4Lightbar(report, offset, 0x00, 0xFF, 0x00);
                        break;
                    default:
                        report[3] = 0xF0 | EffectRumble;
                        break;
                }
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
        /// Sets the lightbar colour in a DualShock 4 effects report. Only meaningful when the
        /// lightbar valid bit is set; the RGB bytes follow the two motor bytes.
        /// </summary>
        private static void SetDualShock4Lightbar(byte[] report, int offset, byte red, byte green, byte blue)
        {
            report[offset + 2] = red;   // ucLedRed
            report[offset + 3] = green; // ucLedGreen
            report[offset + 4] = blue;  // ucLedBlue
        }

        /// <summary>
        /// Steam Controller haptic rumble output report (ID_OUT_REPORT_HAPTIC_RUMBLE): a packed
        /// 10-byte message of type, intensity, and a speed/gain pair per side. This command expires
        /// on its own, which is why <see cref="NeedsResend"/> exists.
        ///
        /// The caller pads the message out to the interface's output report length, which is the
        /// same thing hidapi does on Windows and what SDL relies on when it hands hid_write only
        /// these 10 bytes.
        /// </summary>
        private static byte[] BuildSteamController(int logicalLength, ushort speed)
        {
            if (logicalLength < SteamControllerReportLength)
            {
                return null;
            }

            var report = new byte[SteamControllerReportLength];
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
            public int LogicalLength;
            public int TransmitLength;
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

            [DllImport("hid.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool HidD_GetFeature(SafeFileHandle handle, byte[] reportBuffer, uint reportBufferLength);
        }
    }
}
