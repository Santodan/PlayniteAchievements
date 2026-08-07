using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Playnite.SDK;

namespace PlayniteAchievements.Services.UI
{
    /// <summary>
    /// Pulses every connected controller at a caller-supplied strength and duration.
    /// XInput motors are driven via xinput9_1_0.dll (present on every supported Windows
    /// version), so no package dependency is needed; if the DLL or entry point is missing the
    /// service logs once and permanently no-ops for that backend. Disconnected pads return
    /// ERROR_DEVICE_NOT_CONNECTED from XInputSetState, which is harmless, so all four user
    /// indices are always driven without probing connection state.
    /// Pads that expose no XInput device of their own are driven over raw HID by
    /// <see cref="ControllerHidRumble"/>. A pad routed through XInput emulation (Steam Input,
    /// DS4Windows) is reached by both backends and pulses twice, which is accepted.
    /// </summary>
    internal static class ControllerVibrationService
    {
        private const int MinDurationMs = 100;
        private const int MaxDurationMs = 5000;
        private const int MaxControllers = 4;

        // Matches SDL's resend cadence for the Steam Controller, whose rumble command expires.
        private const int ResendIntervalMs = 40;

        // Serializes motor writes and makes the stop-ownership check atomic: only the newest
        // pulse may zero the motors, so an overlapping wave's pulse is never cut short by an
        // earlier pulse's stop.
        private static readonly object _gate = new object();
        private static int _generation;
        private static volatile bool _xinputUnavailable;

        /// <summary>
        /// Fire-and-forget: vibrates all controllers at <paramref name="strengthPercent"/>
        /// (clamped to 0-100, 0 is a no-op) for <paramref name="durationMs"/> (clamped to
        /// 100-5000 ms), then stops.
        /// </summary>
        public static void Pulse(int strengthPercent, int durationMs, ILogger logger)
        {
            var clamped = Math.Max(0, Math.Min(100, strengthPercent));
            if (clamped == 0)
            {
                return;
            }

            var pulseMs = Math.Max(MinDurationMs, Math.Min(MaxDurationMs, durationMs));
            var speed = (ushort)Math.Round(clamped / 100.0 * ushort.MaxValue);
            Task.Run(async () =>
            {
                try
                {
                    // Handles are opened per pulse and released with it, so a hot-plugged pad is
                    // picked up by the next notification without any cached device state.
                    using (var hid = ControllerHidRumble.Open(logger))
                    {
                        int generation;
                        lock (_gate)
                        {
                            generation = ++_generation;
                            SetAll(speed, hid, logger);
                        }

                        try
                        {
                            if (hid.NeedsResend)
                            {
                                // The Steam Controller's rumble command expires on its own, so it
                                // has to be renewed until the pulse ends.
                                var elapsed = Stopwatch.StartNew();
                                var owner = true;
                                while (owner && elapsed.ElapsedMilliseconds < pulseMs)
                                {
                                    var remaining = Math.Max(1, pulseMs - (int)elapsed.ElapsedMilliseconds);
                                    await Task.Delay(Math.Min(ResendIntervalMs, remaining)).ConfigureAwait(false);
                                    lock (_gate)
                                    {
                                        // A newer pulse has taken over; it owns the stop from here.
                                        owner = _generation == generation;
                                        if (owner)
                                        {
                                            SetAll(speed, hid, logger);
                                        }
                                    }
                                }
                            }
                            else
                            {
                                await Task.Delay(pulseMs).ConfigureAwait(false);
                            }
                        }
                        catch (Exception ex)
                        {
                            logger?.Debug(ex, "Controller vibration wait failed; stopping the pulse.");
                        }

                        // Reached on every path, so a failed wait can never leave motors running.
                        lock (_gate)
                        {
                            if (_generation == generation)
                            {
                                SetAll(0, hid, logger);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger?.Debug(ex, "Controller vibration pulse failed.");
                }
            });
        }

        private static void SetAll(ushort speed, ControllerHidRumble hid, ILogger logger)
        {
            SetAllXInput(speed, logger);
            hid.Set(speed);
        }

        private static void SetAllXInput(ushort speed, ILogger logger)
        {
            if (_xinputUnavailable)
            {
                return;
            }

            var vibration = new NativeMethods.XInputVibration
            {
                LeftMotorSpeed = speed,
                RightMotorSpeed = speed,
            };
            try
            {
                for (uint index = 0; index < MaxControllers; index++)
                {
                    NativeMethods.XInputSetState(index, ref vibration);
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
            {
                _xinputUnavailable = true;
                logger?.Debug(ex, "XInput is unavailable; only HID controllers will vibrate this session.");
            }
        }

        private static class NativeMethods
        {
            [StructLayout(LayoutKind.Sequential)]
            public struct XInputVibration
            {
                public ushort LeftMotorSpeed;
                public ushort RightMotorSpeed;
            }

            [DllImport("xinput9_1_0.dll", EntryPoint = "XInputSetState")]
            public static extern uint XInputSetState(uint dwUserIndex, ref XInputVibration pVibration);
        }
    }
}
