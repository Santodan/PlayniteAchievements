using System;
using System.Runtime.InteropServices;
using SharpDX.MediaFoundation;

namespace PlayniteAchievements.Services.Capture
{
    /// <summary>
    /// Media Foundation's 2D buffer interface, declared by hand because SharpDX's binding of
    /// <c>Lock2D</c> marshals the scanline pointer as a byte array, which loses the pointer the
    /// in-place compositor needs, and its <c>ContiguousCopyTo</c> likewise takes a managed array.
    /// A video buffer implements this whenever its rows are not simply packed at the frame width —
    /// which is the usual case for a decoder's own output surfaces.
    /// </summary>
    [ComImport]
    [Guid("7DC9D5F9-9ED9-44EC-9BBF-0600BB589FBB")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMF2DBuffer
    {
        void Lock2D(out IntPtr scanline0, out int pitch);

        void Unlock2D();

        void GetScanline0AndPitch(out IntPtr scanline0, out int pitch);

        [return: MarshalAs(UnmanagedType.Bool)]
        bool IsContiguousFormat();

        int GetContiguousLength();

        void ContiguousCopyTo(IntPtr destination, int destinationLength);

        void ContiguousCopyFrom(IntPtr source, int sourceLength);
    }

    /// <summary>Borrows a media buffer's <see cref="IMF2DBuffer"/> view, or nothing when it has none.</summary>
    internal struct Buffer2DHandle : IDisposable
    {
        private object _unknown;

        public static Buffer2DHandle From(MediaBuffer buffer)
        {
            var unknown = Marshal.GetObjectForIUnknown(buffer.NativePointer);
            var view = unknown as IMF2DBuffer;
            if (view == null)
            {
                Marshal.ReleaseComObject(unknown);
                return default(Buffer2DHandle);
            }

            return new Buffer2DHandle { _unknown = unknown, Buffer = view };
        }

        public IMF2DBuffer Buffer { get; private set; }

        public bool IsValid => Buffer != null;

        public void Dispose()
        {
            if (_unknown != null)
            {
                Marshal.ReleaseComObject(_unknown);
                _unknown = null;
                Buffer = null;
            }
        }
    }
}
