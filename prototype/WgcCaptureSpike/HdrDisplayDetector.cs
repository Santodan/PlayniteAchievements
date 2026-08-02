using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using static WgcCaptureSpike.NativeInterop;

namespace WgcCaptureSpike
{
    /// <summary>
    /// OS per-monitor HDR detection. Resolves whether "Use HDR" (advanced color) is enabled on the
    /// monitor a given window/rect sits on, via the DisplayConfig APIs. Every failure path returns
    /// false (treat as SDR) — capture must never throw over this. Destined for the plugin at
    /// source/Services/Recording/HdrDisplayDetector.cs (drop the spike namespace + static import).
    /// </summary>
    internal static class HdrDisplayDetector
    {
        // HDR is toggled around game launch, so a whole-session cache would go stale; a short TTL
        // collapses a burst of unlock captures into one query while still following a mid-session
        // toggle on the next window resolve.
        private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(10);

        private static readonly ConcurrentDictionary<string, (bool Hdr, DateTime StampUtc)> Cache =
            new ConcurrentDictionary<string, (bool, DateTime)>(StringComparer.OrdinalIgnoreCase);

        /// <summary>HDR state of the monitor hosting <paramref name="hwnd"/>.</summary>
        public static bool IsHdrActive(IntPtr hwnd)
        {
            try
            {
                var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                return IsHdrActiveForMonitor(monitor);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>HDR state of the monitor hosting <paramref name="bounds"/> (physical pixels).</summary>
        public static bool IsHdrActive(RECT bounds)
        {
            try
            {
                var monitor = MonitorFromRect(ref bounds, MONITOR_DEFAULTTONEAREST);
                return IsHdrActiveForMonitor(monitor);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsHdrActiveForMonitor(IntPtr monitor)
        {
            if (monitor == IntPtr.Zero)
            {
                return false;
            }

            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf(typeof(MONITORINFOEX)) };
            if (!GetMonitorInfoW(monitor, ref info) || string.IsNullOrEmpty(info.szDevice))
            {
                return false;
            }

            var device = info.szDevice;
            if (Cache.TryGetValue(device, out var cached) && DateTime.UtcNow - cached.StampUtc < CacheTtl)
            {
                return cached.Hdr;
            }

            var hdr = QueryHdrForDevice(device);
            Cache[device] = (hdr, DateTime.UtcNow);
            return hdr;
        }

        /// <summary>
        /// Walks the active display paths, matches the one whose source GDI device name equals
        /// <paramref name="gdiDeviceName"/> (e.g. \\.\DISPLAY1), and reads advancedColorEnabled off
        /// that path's target.
        /// </summary>
        private static bool QueryHdrForDevice(string gdiDeviceName)
        {
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out var pathCount, out var modeCount) != 0)
            {
                return false;
            }

            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
            if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0)
            {
                return false;
            }

            for (var i = 0; i < pathCount; i++)
            {
                var path = paths[i];

                var sourceName = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
                {
                    header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                        size = Marshal.SizeOf(typeof(DISPLAYCONFIG_SOURCE_DEVICE_NAME)),
                        adapterId = path.sourceInfo.adapterId,
                        id = path.sourceInfo.id
                    }
                };

                if (DisplayConfigGetDeviceInfo(ref sourceName) != 0)
                {
                    continue;
                }

                if (!string.Equals(sourceName.viewGdiDeviceName, gdiDeviceName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var colorInfo = new DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
                {
                    header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type = DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO,
                        size = Marshal.SizeOf(typeof(DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO)),
                        adapterId = path.targetInfo.adapterId,
                        id = path.targetInfo.id
                    }
                };

                if (DisplayConfigGetDeviceInfo(ref colorInfo) != 0)
                {
                    return false;
                }

                return colorInfo.AdvancedColorEnabled;
            }

            return false;
        }
    }
}
