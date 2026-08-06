using System;
using System.Drawing;

namespace PlayniteAchievements.Services.Capture
{
    /// <summary>
    /// Stands in for the real Windows.Graphics.Capture implementation, which UnlockScreenshotService
    /// calls into and which cannot be compiled here: it needs WinRT projections and SharpDX D3D11
    /// interop that a unit-test assembly has no reason to carry.
    /// </summary>
    /// <remarks>
    /// <see cref="IsSupported"/> is always false, so any test touching the capture path
    /// deterministically takes the GDI fallback and never reaches a real capture device. Only the
    /// members UnlockScreenshotService uses are present.
    /// </remarks>
    internal sealed class WgcWindowCapture : IDisposable
    {
        public static bool IsSupported => false;

        public CapturedFrame CaptureWindow(IntPtr hwnd) => null;

        public CapturedFrame CaptureMonitorForWindow(IntPtr windowOnMonitor) => null;

        public void Dispose()
        {
        }

        internal sealed class CapturedFrame
        {
            public Bitmap Bitmap { get; set; }
        }
    }
}
