using System;
using System.Collections.Generic;
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
    /// sits directly above the cloaked window in the z-order and takes that window's rectangle, so
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
        private const int GwlExStyle = -20;
        private const int WsExTopmost = 0x8;
        private static readonly IntPtr HwndTopmost = new IntPtr(-1);

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

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int GetWindowLong(IntPtr hwnd, int index);

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

        /// <summary>One registered thumbnail and what was last sent for it.</summary>
        private sealed class Thumb
        {
            public IntPtr Handle;
            public bool HasPushed;
            public ThumbnailProperties Pushed;
        }

        private readonly Window _source;
        private readonly IntPtr _sourceHwnd;
        private readonly Thumb _main = new Thumb();
        private readonly List<Thumb> _layers = new List<Thumb>();

        // A layer drawn under the main source. Thumbnails stack in the order they were registered,
        // and the main one was first, so the underlay is followed by a second thumbnail of the main
        // source, which stands in for the first while the layers are shown.
        private Thumb _underlay;
        private Thumb _mainOver;
        private Surface _surface;
        private IntPtr _thumbnail;
        private RectI _rect;
        private bool _placed;
        private bool _revealed;
        private bool _layersShown;
        private RectI _destination;
        private byte _level;

        private ToastThumbnailPresenter(Window source, IntPtr sourceHwnd, Surface surface, IntPtr thumbnail)
        {
            _source = source;
            _sourceHwnd = sourceHwnd;
            _surface = surface;
            _thumbnail = thumbnail;
            _main.Handle = thumbnail;
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
                Push(presenter._main, new RectI(), 0, false);
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

        /// <summary>
        /// A factor on the main thumbnail's opacity alone, 1 by default. The layers over it are
        /// not dimmed by it.
        /// </summary>
        public double MainOpacityScale { get; set; } = 1d;

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
                _destination = destination;
                _level = level;
                var visible = _revealed && level > 0;
                var scale = MainOpacityScale;
                var mainLevel = scale >= 1d || double.IsNaN(scale)
                    ? level
                    : (byte)Math.Round(level * Math.Max(0d, scale));
                var under = _underlay != null && _layersShown;
                Push(_main, destination, mainLevel, visible && !under);
                if (_underlay != null)
                {
                    Push(_underlay, destination, level, visible && under);
                    Push(_mainOver, destination, mainLevel, visible && under);
                }

                foreach (var layer in _layers)
                {
                    Push(layer, destination, level, visible && _layersShown);
                }
            }
            catch
            {
            }
        }

        /// <summary>
        /// Registers <paramref name="source"/>, a cloaked window of the main source's size, as a
        /// further thumbnail drawn over those registered before it, hidden until
        /// <see cref="ShowLayers"/>. False when it cannot be registered or differs in size.
        /// </summary>
        public bool AddLayer(Window source)
        {
            try
            {
                var surface = _surface;
                var hwnd = source == null ? IntPtr.Zero : new WindowInteropHelper(source).Handle;
                if (surface == null || hwnd == IntPtr.Zero)
                {
                    return false;
                }

                using (Common.DpiAwarenessScope.PerMonitorV2())
                {
                    if (!GetWindowRect(hwnd, out var rect) || !GetWindowRect(_sourceHwnd, out var mainRect) ||
                        rect.Right - rect.Left != mainRect.Right - mainRect.Left ||
                        rect.Bottom - rect.Top != mainRect.Bottom - mainRect.Top)
                    {
                        return false;
                    }
                }

                if (DwmGetWindowAttribute(hwnd, DwmwaCloaked, out var cloaked, sizeof(int)) != 0 || cloaked == 0 ||
                    DwmRegisterThumbnail(surface.Handle, hwnd, out var thumbnail) != 0 || thumbnail == IntPtr.Zero)
                {
                    return false;
                }

                var layer = new Thumb { Handle = thumbnail };
                Push(layer, _destination, _level, false);
                _layers.Add(layer);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Registers <paramref name="source"/>, a cloaked window of the main source's size, to be
        /// drawn under the main source while the layers are shown. Call before any
        /// <see cref="AddLayer"/>. False when it cannot be registered.
        /// </summary>
        public bool AddUnderlay(Window source)
        {
            if (_underlay != null || _layers.Count > 0 || !AddLayer(source))
            {
                return false;
            }

            _underlay = _layers[0];
            _layers.Clear();
            try
            {
                if (DwmRegisterThumbnail(_surface.Handle, _sourceHwnd, out var again) == 0 && again != IntPtr.Zero)
                {
                    _mainOver = new Thumb { Handle = again };
                    Push(_mainOver, _destination, _level, false);
                    return true;
                }
            }
            catch
            {
            }

            try
            {
                DwmUnregisterThumbnail(_underlay.Handle);
            }
            catch
            {
            }

            _underlay = null;
            return false;
        }

        /// <summary>
        /// Shows the layers over the main thumbnail, which stays underneath. The top layer appears
        /// first, so if the calls land in different compositions the content is never missing over
        /// a layer below it.
        /// </summary>
        public void ShowLayers()
        {
            _layersShown = true;
            var visible = _revealed && _level > 0;
            if (_underlay != null)
            {
                // The second main thumbnail shows what the first does, so the first can go once
                // the second is there.
                var mainLevel = _main.HasPushed ? _main.Pushed.Opacity : _level;
                Push(_mainOver, _destination, mainLevel, visible);
                Push(_underlay, _destination, _level, visible);
                Push(_main, _destination, mainLevel, false);
            }

            for (var i = _layers.Count - 1; i >= 0; i--)
            {
                Push(_layers[i], _destination, _level, visible);
            }
        }

        /// <summary>Hides and unregisters the layers, lowest first, leaving the main thumbnail.</summary>
        public void RemoveLayers()
        {
            _layersShown = false;
            if (_underlay != null)
            {
                try
                {
                    var mainLevel = _mainOver.HasPushed ? _mainOver.Pushed.Opacity : _level;
                    Push(_main, _destination, mainLevel, _revealed && _level > 0);
                    Push(_mainOver, _destination, mainLevel, false);
                    Push(_underlay, _destination, _level, false);
                    DwmUnregisterThumbnail(_mainOver.Handle);
                    DwmUnregisterThumbnail(_underlay.Handle);
                }
                catch
                {
                }

                _underlay = null;
                _mainOver = null;
            }

            foreach (var layer in _layers)
            {
                try
                {
                    Push(layer, _destination, _level, false);
                    DwmUnregisterThumbnail(layer.Handle);
                }
                catch
                {
                }
            }

            _layers.Clear();
        }

        public void Dispose()
        {
            RemoveLayers();
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

        private static void Push(Thumb thumb, RectI destination, byte opacity, bool visible)
        {
            if (thumb.HasPushed &&
                thumb.Pushed.Visible == visible && thumb.Pushed.Opacity == opacity &&
                thumb.Pushed.Destination.Left == destination.Left && thumb.Pushed.Destination.Top == destination.Top &&
                thumb.Pushed.Destination.Right == destination.Right && thumb.Pushed.Destination.Bottom == destination.Bottom)
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
            if (DwmUpdateThumbnailProperties(thumb.Handle, ref properties) == 0)
            {
                thumb.Pushed = properties;
                thumb.HasPushed = true;
            }
        }

        /// <summary>
        /// Keeps the presenting window on the source's rectangle and directly above it in the
        /// z-order, which is where the toast's own placement expects the source to stay: the
        /// window directly above the game. Read and set as physical pixels, like every other placement of the toast.
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
                // Directly above the source: SetWindowPos places a window below the one named, so
                // this one goes below whatever is above the source now, or to the top of the
                // source's band when nothing is.
                var above = GetWindow(_sourceHwnd, GwHwndPrev);
                if (!moved && above == surface.Handle)
                {
                    return;
                }

                var insertAfter = above;
                if (above == surface.Handle)
                {
                    insertAfter = GetWindow(surface.Handle, GwHwndPrev);
                }

                if (insertAfter == IntPtr.Zero)
                {
                    insertAfter = (GetWindowLong(_sourceHwnd, GwlExStyle) & WsExTopmost) != 0 ? HwndTopmost : IntPtr.Zero;
                }

                if (SetWindowPos(
                    surface.Handle, insertAfter, rect.Left, rect.Top,
                    rect.Right - rect.Left, rect.Bottom - rect.Top, SwpNoActivate))
                {
                    _rect = rect;
                    _placed = true;
                }
            }
        }
    }
}
