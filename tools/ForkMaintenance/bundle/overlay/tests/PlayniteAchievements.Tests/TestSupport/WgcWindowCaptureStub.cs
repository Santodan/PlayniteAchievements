using System;
using System.Drawing;

namespace PlayniteAchievements.Services.Capture
{
    public sealed class WgcWindowCapture : IDisposable
    {
        public static bool IsSupported => false;

        public CaptureResult CaptureWindow(IntPtr hwnd, int warmupMs = 150) => null;

        public CaptureResult CaptureMonitorForWindow(IntPtr hwnd, int warmupMs = 150) => null;

        public void Dispose()
        {
        }
    }

    public sealed class CaptureResult
    {
        public Bitmap Bitmap { get; set; }
    }
}
