using System.Runtime.InteropServices;

namespace PlayniteAchievements.Services.Capture
{
    internal static class Hr
    {
        /// <summary>Throws a COMException carrying the HRESULT when a raw Win32/COM call fails.</summary>
        public static void CheckWin32(this int hr, string what)
        {
            if (hr != 0)
            {
                throw new COMException($"{what} failed (0x{hr:X8})", hr);
            }
        }
    }
}
