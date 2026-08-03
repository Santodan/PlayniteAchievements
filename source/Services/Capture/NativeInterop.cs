using System;
using System.Runtime.InteropServices;

namespace PlayniteAchievements.Services.Capture
{
    /// <summary>
    /// Raw Win32 / COM interop the WGC path needs beyond what the WinRT projection and Vortice
    /// give us: the activation-factory interop interface that turns an HWND into a
    /// GraphicsCaptureItem, the DXGI-interface accessor that unwraps a WinRT surface back to a
    /// D3D11 texture, and the d3d11.dll shim that wraps a DXGI device as a WinRT IDirect3DDevice.
    /// </summary>
    internal static class NativeInterop
    {
        // windows.graphics.capture.interop.h — IGraphicsCaptureItemInterop.
        // The activation factory of Windows.Graphics.Capture.GraphicsCaptureItem implements this;
        // CreateForWindow is the only supported way to capture a specific HWND from Win32.
        [ComImport]
        [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IGraphicsCaptureItemInterop
        {
            IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);

            IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
        }

        // windows.graphics.directx.direct3d11.interop.h — IDirect3DDxgiInterfaceAccess.
        // A WinRT IDirect3DSurface / IDirect3DDevice implements this; GetInterface hands back the
        // underlying DXGI/D3D11 object (e.g. ID3D11Texture2D) so we can copy + map it.
        [ComImport]
        [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IDirect3DDxgiInterfaceAccess
        {
            IntPtr GetInterface([In] ref Guid iid);
        }

        // IID of ID3D11Texture2D — passed to IDirect3DDxgiInterfaceAccess.GetInterface.
        internal static readonly Guid IID_ID3D11Texture2D =
            new Guid("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

        // IID of the WinRT GraphicsCaptureItem runtime class — passed to CreateForWindow.
        internal static readonly Guid IID_IGraphicsCaptureItem =
            new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");

        /// <summary>
        /// Wraps a DXGI device as a WinRT IDirect3DDevice (an IInspectable). We hand the returned
        /// pointer to the WinRT projection to get the IDirect3DDevice the frame pool needs.
        /// </summary>
        [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", SetLastError = true)]
        internal static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

        // --- Window / monitor geometry used by HdrDisplayDetector and window resolution. ---

        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        // Visible window bounds excluding the invisible resize border/shadow — this matches the
        // region WGC CreateForWindow captures, so client-area cropping measures against it.
        [DllImport("dwmapi.dll")]
        internal static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

        internal const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

        [StructLayout(LayoutKind.Sequential)]
        internal struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern int GetWindowTextW(IntPtr hWnd, [Out] char[] lpString, int nMaxCount);

        [DllImport("user32.dll")]
        internal static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll")]
        internal static extern IntPtr MonitorFromRect([In] ref RECT lprc, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEX lpmi);

        internal const uint MONITOR_DEFAULTTONEAREST = 2;

        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;

            public int Width => Right - Left;

            public int Height => Bottom - Top;

            public override string ToString() => $"({Left},{Top})-({Right},{Bottom}) {Width}x{Height}";
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }

        // --- DisplayConfig APIs for HDR detection (advancedColorEnabled). ---

        [DllImport("user32.dll")]
        internal static extern int GetDisplayConfigBufferSizes(
            uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

        [DllImport("user32.dll")]
        internal static extern int QueryDisplayConfig(
            uint flags,
            ref uint numPathArrayElements,
            [Out] DISPLAYCONFIG_PATH_INFO[] pathInfoArray,
            ref uint numModeInfoArrayElements,
            [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
            IntPtr currentTopologyId);

        [DllImport("user32.dll")]
        internal static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

        [DllImport("user32.dll")]
        internal static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO requestPacket);

        [DllImport("user32.dll")]
        internal static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SDR_WHITE_LEVEL requestPacket);

        internal const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;

        // DISPLAYCONFIG_DEVICE_INFO_TYPE values.
        internal const int DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
        internal const int DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO = 9;
        internal const int DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL = 11;

        [StructLayout(LayoutKind.Sequential)]
        internal struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct DISPLAYCONFIG_DEVICE_INFO_HEADER
        {
            public int type;
            public int size;
            public LUID adapterId;
            public uint id;
        }

        // We only read adapterId/id from sourceInfo and targetInfo; the rest is sized correctly so
        // QueryDisplayConfig marshals the arrays. Total size must match the native struct (48 bytes
        // on x64 for PATH_INFO).
        [StructLayout(LayoutKind.Sequential)]
        internal struct DISPLAYCONFIG_PATH_SOURCE_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx; // union with cloneGroupId/sourceModeInfoIdx on newer SDKs
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct DISPLAYCONFIG_PATH_TARGET_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public int outputTechnology;
            public int rotation;
            public int scaling;
            public DISPLAYCONFIG_RATIONAL refreshRate;
            public int scanLineOrdering;
            [MarshalAs(UnmanagedType.Bool)]
            public bool targetAvailable;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct DISPLAYCONFIG_RATIONAL
        {
            public uint Numerator;
            public uint Denominator;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct DISPLAYCONFIG_PATH_INFO
        {
            public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
            public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
            public uint flags;
        }

        // DISPLAYCONFIG_MODE_INFO is a tagged union; we never read its contents, only need the
        // array sized correctly (64 bytes each on x64). Reserve the payload as a fixed buffer.
        [StructLayout(LayoutKind.Sequential)]
        internal struct DISPLAYCONFIG_MODE_INFO
        {
            public int infoType;
            public uint id;
            public LUID adapterId;
            public DISPLAYCONFIG_MODE_INFO_UNION modeUnion;
        }

        [StructLayout(LayoutKind.Sequential, Size = 48)]
        internal struct DISPLAYCONFIG_MODE_INFO_UNION
        {
            // 48-byte payload covering the largest union member (source/target/desktop image mode).
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string viewGdiDeviceName;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;

            // Packed bitfield: bit0 advancedColorSupported, bit1 advancedColorEnabled,
            // bit2 wideColorEnforced, bit3 advancedColorForceDisabled.
            public uint value;
            public int colorEncoding;
            public int bitsPerColorChannel;

            public bool AdvancedColorSupported => (value & 0x1) != 0;

            public bool AdvancedColorEnabled => (value & 0x2) != 0;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct DISPLAYCONFIG_SDR_WHITE_LEVEL
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;

            // SDR white level: nits = SDRWhiteLevel / 1000 * 80, so the scRGB reference white
            // (1.0 = 80 nits) is simply SDRWhiteLevel / 1000.
            public uint SDRWhiteLevel;
        }
    }
}
