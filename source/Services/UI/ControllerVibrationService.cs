using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Playnite.SDK;

namespace PlayniteAchievements.Services.UI
{
    /// <summary>
    /// Pulses every connected XInput controller for a short fixed duration at a caller-supplied
    /// strength. Motors are driven via xinput9_1_0.dll (present on every supported Windows
    /// version), so no package dependency is needed; if the DLL or entry point is missing the
    /// service logs once and permanently no-ops. Disconnected pads return
    /// ERROR_DEVICE_NOT_CONNECTED from XInputSetState, which is harmless, so all four user
    /// indices are always driven without probing connection state.
    /// </summary>
    internal static class ControllerVibrationService
    {
        private const int PulseDurationMs = 400;
        private const int MaxControllers = 4;

        // Serializes motor writes and makes the stop-ownership check atomic: only the newest
        // pulse may zero the motors, so an overlapping wave's pulse is never cut short by an
        // earlier pulse's stop.
        private static readonly object _gate = new object();
        private static int _generation;
        private static volatile bool _unavailable;

        /// <summary>
        /// Fire-and-forget: vibrates all controllers at <paramref name="strengthPercent"/>
        /// (clamped to 0-100, 0 is a no-op) for <see cref="PulseDurationMs"/>, then stops.
        /// </summary>
        public static void Pulse(int strengthPercent, ILogger logger)
        {
            if (_unavailable)
            {
                return;
            }

            var clamped = Math.Max(0, Math.Min(100, strengthPercent));
            if (clamped == 0)
            {
                return;
            }

            var speed = (ushort)Math.Round(clamped / 100.0 * ushort.MaxValue);
            Task.Run(async () =>
            {
                try
                {
                    int generation;
                    lock (_gate)
                    {
                        generation = ++_generation;
                        SetAll(speed);
                    }

                    await Task.Delay(PulseDurationMs).ConfigureAwait(false);
                    lock (_gate)
                    {
                        if (_generation == generation)
                        {
                            SetAll(0);
                        }
                    }
                }
                catch (DllNotFoundException ex)
                {
                    _unavailable = true;
                    logger?.Debug(ex, "XInput is unavailable; controller vibration disabled for this session.");
                }
                catch (EntryPointNotFoundException ex)
                {
                    _unavailable = true;
                    logger?.Debug(ex, "XInput is unavailable; controller vibration disabled for this session.");
                }
            });
        }

        private static void SetAll(ushort speed)
        {
            var vibration = new NativeMethods.XInputVibration
            {
                LeftMotorSpeed = speed,
                RightMotorSpeed = speed,
            };
            for (uint index = 0; index < MaxControllers; index++)
            {
                NativeMethods.XInputSetState(index, ref vibration);
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
