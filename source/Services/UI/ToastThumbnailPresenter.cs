using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;
using System.Windows.Media;

namespace PlayniteAchievements.Services.UI
{
    /// <summary>
    /// Shows a cloaked notification window on screen through a DWM thumbnail in a second window, and
    /// moves, scales and fades that thumbnail in place of the card.
    ///
    /// A motion drawn by WPF needs a WPF frame per step, and under a game that keeps the GPU busy
    /// those frames queue behind the game's (tools/capture-harness/README.md, "The thumbnail probe").
    /// A thumbnail is composed by DWM from the surface the cloaked window already presented, so a
    /// step is one <c>DwmUpdateThumbnailProperties</c> call and no GPU work in this process. On
    /// screen the thumbnail matched the window itself pixel for pixel, per-pixel alpha included.
    ///
    /// The presenting window is layered with a colour key over its own fill, so nothing of it shows
    /// but the thumbnail, and transparent to input, so every click reaches what is underneath. It
    /// sits directly below the cloaked window in the z-order and takes that window's rectangle, so
    /// it clips the thumbnail exactly as the window clips its content.
    ///
    /// UI thread only. All members are wrapped so they can never throw into the toast pipeline.
    /// </summary>
    internal sealed class ToastThumbnailPresenter : IDisposable
    {
        private const int WsPopup = unchecked((int)0x80000000);
        private const int WsExTransparent = 0x20;
        private const int WsExToolWindow = 0x80;
        private const int WsExLayered = 0x80000;
        private const int WsExNoActivate = 0x08000000;
        private const int WmEraseBackground = 0x0014;
        private const int WmMouseActivate = 0x0021;
        private const int MaNoActivate = 3;
        private const int SwShowNoActivate = 4;
        private const uint LwaColorKey = 1;
        private const uint SwpNoActivate = 0x0010;
        private const uint GwHwndPrev = 3;
        private const int DwmwaCloaked = 14;
        private const int BlackBrush = 4;

        private const int TnpRectDestination = 0x1;
        private const int TnpOpacity = 0x4;
        private const int TnpVisible = 0x8;
        private const int TnpSourceClientAreaOnly = 0x10;

        [StructLayout(LayoutKind.Sequential)]
        private struct RectI
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ThumbnailProperties
        {
            public int Flags;
            public RectI Destination;
            public RectI Source;
            public byte Opacity;
            [MarshalAs(UnmanagedType.Bool)] public bool Visible;
            [MarshalAs(UnmanagedType.Bool)] public bool SourceClientAreaOnly;
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmRegisterThumbnail(IntPtr destination, IntPtr source, out IntPtr thumbnail);

        [DllImport("dwmapi.dll")]
        private static extern int DwmUnregisterThumbnail(IntPtr thumbnail);

        [DllImport("dwmapi.dll")]
        private static extern int DwmUpdateThumbnailProperties(IntPtr thumbnail, ref ThumbnailProperties properties);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hwnd, out RectI rect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetClientRect(IntPtr hwnd, out RectI rect);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr hwnd, int command);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint colorKey, byte alpha, uint flags);

        [DllImport("user32.dll")]
        private static extern int FillRect(IntPtr dc, ref RectI rect, IntPtr brush);

        [DllImport("gdi32.dll")]
        private static extern IntPtr GetStockObject(int index);

        /// <summary>A bare popup filled black, which the colour key then removes entirely.</summary>
        private sealed class Surface : NativeWindow
        {
            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WmEraseBackground)
                {
                    if (GetClientRect(m.HWnd, out var client))
                    {
                        FillRect(m.WParam, ref client, GetStockObject(BlackBrush));
                    }

                    m.Result = new IntPtr(1);
                    return;
                }

                if (m.Msg == WmMouseActivate)
                {
                    m.Result = new IntPtr(MaNoActivate);
                    return;
                }

                base.WndProc(ref m);
            }
        }

        private readonly Window _source;
        private readonly IntPtr _sourceHwnd;
        private Surface _surface;
        private IntPtr _thumbnail;
        private RectI _rect;
        private bool _placed;
        private bool _revealed;
        private bool _hasPushed;
        private ThumbnailProperties _pushed;

        private ToastThumbnailPresenter(Window source, IntPtr sourceHwnd, Surface surface, IntPtr thumbnail)
        {
            _source = source;
            _sourceHwnd = sourceHwnd;
            _surface = surface;
            _thumbnail = thumbnail;
        }

        /// <summary>
        /// Cloaking, which keeps the presented window itself off screen, exists from Windows 8.
        /// </summary>
        public static bool IsSupported
        {
            get
            {
                var version = Environment.OSVersion.Version;
                return version.Major > 6 || (version.Major == 6 && version.Minor >= 2);
            }
        }

        /// <summary>
        /// Creates the presenting window for <paramref name="source"/>, hidden until
        /// <see cref="Reveal"/>, or returns null when the thumbnail cannot be set up. The window is
        /// created under the calling thread's DPI awareness, so create it where the source's handle
        /// was created. <paramref name="source"/> needs its handle and must be cloaked.
        /// </summary>
        public static ToastThumbnailPresenter TryCreate(Window source)
        {
            Surface surface = null;
            try
            {
                var sourceHwnd = source == null ? IntPtr.Zero : new WindowInteropHelper(source).Handle;
                if (sourceHwnd == IntPtr.Zero || !IsSupported)
                {
                    return null;
                }

                // An uncloaked source would show twice, once as itself and once as its thumbnail.
                if (DwmGetWindowAttribute(sourceHwnd, DwmwaCloaked, out var cloaked, sizeof(int)) != 0 || cloaked == 0)
                {
                    return null;
                }

                surface = new Surface();
                surface.CreateHandle(new CreateParams
                {
                    Caption = string.Empty,
                    Style = WsPopup,
                    ExStyle = WsExLayered | WsExTransparent | WsExToolWindow | WsExNoActivate,
                    X = 0,
                    Y = 0,
                    Width = 1,
                    Height = 1,
                });

                if (!SetLayeredWindowAttributes(surface.Handle, 0, 0, LwaColorKey) ||
                    DwmRegisterThumbnail(surface.Handle, sourceHwnd, out var thumbnail) != 0 ||
                    thumbnail == IntPtr.Zero)
                {
                    surface.DestroyHandle();
                    return null;
                }

                var presenter = new ToastThumbnailPresenter(source, sourceHwnd, surface, thumbnail);
                presenter.Push(new RectI(), 0, false);
                presenter.Follow();
                ShowWindow(surface.Handle, SwShowNoActivate);
                return presenter;
            }
            catch
            {
                try
                {
                    surface?.DestroyHandle();
                }
                catch
                {
                }

                return null;
            }
        }

        /// <summary>Lets the thumbnail show from the next <see cref="Update"/> on.</summary>
        public void Reveal()
        {
            _revealed = true;
        }

        /// <summary>
        /// Takes the source window's rectangle and its place in the z-order, then shows the thumbnail
        /// under <paramref name="motion"/> (the motion's transform in the source window's DIPs, about
        /// the window's top-left) at <paramref name="opacity"/>. Does nothing when neither changed.
        /// A motion with rotation or skew is shown by its scale and offset alone.
        /// </summary>
        public void Update(Matrix motion, double opacity)
        {
            if (_thumbnail == IntPtr.Zero)
            {
                return;
            }

            try
            {
                Follow();
                var width = _rect.Right - _rect.Left;
                var height = _rect.Bottom - _rect.Top;
                var dipWidth = _source.ActualWidth;
                var dipHeight = _source.ActualHeight;
                if (!_placed || width <= 0 || height <= 0 || dipWidth <= 0 || dipHeight <= 0)
                {
                    return;
                }

                var pxPerDipX = width / dipWidth;
                var pxPerDipY = height / dipHeight;
                var left = (int)Math.Round(motion.OffsetX * pxPerDipX);
                var top = (int)Math.Round(motion.OffsetY * pxPerDipY);
                var destination = new RectI
                {
                    Left = left,
                    Top = top,
                    Right = left + Math.Max(1, (int)Math.Round(width * Math.Abs(motion.M11))),
                    Bottom = top + Math.Max(1, (int)Math.Round(height * Math.Abs(motion.M22))),
                };

                var level = (byte)Math.Round(255d * Math.Max(0d, Math.Min(1d, double.IsNaN(opacity) ? 1d : opacity)));
                Push(destination, level, _revealed && level > 0);
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            try
            {
                if (_thumbnail != IntPtr.Zero)
                {
                    DwmUnregisterThumbnail(_thumbnail);
                    _thumbnail = IntPtr.Zero;
                }
            }
            catch
            {
            }

            try
            {
                _surface?.DestroyHandle();
            }
            catch
            {
            }

            _surface = null;
        }

        private void Push(RectI destination, byte opacity, bool visible)
        {
            if (_hasPushed &&
                _pushed.Visible == visible && _pushed.Opacity == opacity &&
                _pushed.Destination.Left == destination.Left && _pushed.Destination.Top == destination.Top &&
                _pushed.Destination.Right == destination.Right && _pushed.Destination.Bottom == destination.Bottom)
            {
                return;
            }

            var properties = new ThumbnailProperties
            {
                Flags = TnpRectDestination | TnpOpacity | TnpVisible | TnpSourceClientAreaOnly,
                Destination = destination,
                Opacity = opacity,
                Visible = visible,
                SourceClientAreaOnly = true,
            };
            if (DwmUpdateThumbnailProperties(_thumbnail, ref properties) == 0)
            {
                _pushed = properties;
                _hasPushed = true;
            }
        }

        /// <summary>
        /// Keeps the presenting window on the source's rectangle and directly below it in the
        /// z-order. Read and set as physical pixels, like every other placement of the toast.
        /// </summary>
        private void Follow()
        {
            var surface = _surface;
            if (surface == null)
            {
                return;
            }

            using (Common.DpiAwarenessScope.PerMonitorV2())
            {
                if (!GetWindowRect(_sourceHwnd, out var rect))
                {
                    return;
                }

                var moved = !_placed ||
                    rect.Left != _rect.Left || rect.Top != _rect.Top ||
                    rect.Right != _rect.Right || rect.Bottom != _rect.Bottom;
                if (!moved && GetWindow(surface.Handle, GwHwndPrev) == _sourceHwnd)
                {
                    return;
                }

                if (SetWindowPos(
                    surface.Handle, _sourceHwnd, rect.Left, rect.Top,
                    rect.Right - rect.Left, rect.Bottom - rect.Top, SwpNoActivate))
                {
                    _rect = rect;
                    _placed = true;
                }
            }
        }
    }
}
