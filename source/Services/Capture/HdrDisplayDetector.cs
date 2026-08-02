using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using static PlayniteAchievements.Services.Capture.NativeInterop;

namespace PlayniteAchievements.Services.Capture
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

        private static readonly ConcurrentDictionary<string, CacheEntry> Cache =
            new ConcurrentDictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);

        // A plain struct rather than a value tuple so this assembly needs no System.ValueTuple
        // reference (keeps its output to just its own DLL + SharpDX, no netstandard facades).
        private struct CacheEntry
        {
            public bool Hdr;
            public DateTime StampUtc;
        }

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

        /// <summary>
        /// The monitor's SDR white level as an scRGB reference (1.0 = 80 nits). On an HDR desktop
        /// Windows renders SDR content at this elevated white, so dividing the captured scRGB by it
        /// maps SDR content back to 1.0 (correct exposure) and leaves only real HDR highlights above
        /// 1.0. Returns 1.0 when unavailable (SDR display / query failure).
        /// </summary>
        public static float GetSdrWhiteScRgb(IntPtr hwnd)
        {
            try
            {
                var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                if (monitor == IntPtr.Zero)
                {
                    return 1.0f;
                }

                var info = new MONITORINFOEX { cbSize = Marshal.SizeOf(typeof(MONITORINFOEX)) };
                if (!GetMonitorInfoW(monitor, ref info) || string.IsNullOrEmpty(info.szDevice))
                {
                    return 1.0f;
                }

                return QuerySdrWhiteForDevice(info.szDevice);
            }
            catch
            {
                return 1.0f;
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
            Cache[device] = new CacheEntry { Hdr = hdr, StampUtc = DateTime.UtcNow };
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

        private static float QuerySdrWhiteForDevice(string gdiDeviceName)
        {
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out var pathCount, out var modeCount) != 0)
            {
                return 1.0f;
            }

            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
            if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0)
            {
                return 1.0f;
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

                if (DisplayConfigGetDeviceInfo(ref sourceName) != 0 ||
                    !string.Equals(sourceName.viewGdiDeviceName, gdiDeviceName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var whiteLevel = new DISPLAYCONFIG_SDR_WHITE_LEVEL
                {
                    header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type = DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL,
                        size = Marshal.SizeOf(typeof(DISPLAYCONFIG_SDR_WHITE_LEVEL)),
                        adapterId = path.targetInfo.adapterId,
                        id = path.targetInfo.id
                    }
                };

                if (DisplayConfigGetDeviceInfo(ref whiteLevel) != 0 || whiteLevel.SDRWhiteLevel == 0)
                {
                    return 1.0f;
                }

                // scRGB reference white = SDRWhiteLevel / 1000 (1.0 = 80 nits). Floor at 1.0.
                return Math.Max(1.0f, whiteLevel.SDRWhiteLevel / 1000f);
            }

            return 1.0f;
        }
    }
}
