using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using Playnite.SDK;
using PlayniteAchievements.Common;

namespace PlayniteAchievements.Views.Helpers
{
    public static class PlayniteUiProvider
    {
        private const string FullscreenWindowTag = "PlayniteAchievementsFullscreen";

        public static void RestoreMainView()
        {
            API.Instance.MainView.SwitchToLibraryView();
        }

        public static void HandleEsc(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                if (sender is Window window)
                {
                    e.Handled = true;
                    window.Close();
                }
            }
        }

        public static Window CreateExtensionWindow(string Title, UserControl ViewExtension, WindowOptions windowOptions = null, bool isFullscreen = false)
        {
            if (windowOptions == null)
            {
                windowOptions = new WindowOptions
                {
                    ShowMinimizeButton = false,
                    ShowMaximizeButton = false,
                    ShowCloseButton = true
                };
            }

            if (isFullscreen)
            {
                return CreateFullscreenWindow(Title, ViewExtension, windowOptions);
            }

            Window windowExtension = API.Instance.Dialogs.CreateWindow(windowOptions);

            FormattingCulture.Apply(windowExtension);
            windowExtension.Title = Title;
            windowExtension.ShowInTaskbar = false;
            windowExtension.ResizeMode = windowOptions.CanBeResizable ? ResizeMode.CanResize : ResizeMode.NoResize;
            windowExtension.Owner = API.Instance.Dialogs.GetCurrentAppWindow();
            windowExtension.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            windowExtension.Content = ViewExtension;

            if (!double.IsNaN(ViewExtension.Height) && !double.IsNaN(ViewExtension.Width))
            {
                windowExtension.Height = ViewExtension.Height + 25;
                windowExtension.Width = ViewExtension.Width;
            }
            else if (!double.IsNaN(ViewExtension.MinHeight) && !double.IsNaN(ViewExtension.MinWidth) && ViewExtension.MinHeight > 0 && ViewExtension.MinWidth > 0)
            {
                windowExtension.Height = ViewExtension.MinHeight + 25;
                windowExtension.Width = ViewExtension.MinWidth;
            }
            else if (windowOptions.Width != 0 && windowOptions.Height != 0)
            {
                windowExtension.Width = windowOptions.Width;
                windowExtension.Height = windowOptions.Height;
            }
            else
            {
                windowExtension.SizeToContent = SizeToContent.WidthAndHeight;
            }

            windowExtension.PreviewKeyDown += new KeyEventHandler(HandleEsc);

            ApplyWindowThemeBrushes(windowExtension);

            return windowExtension;
        }

        // Retheme the Playnite-drawn window chrome (title strip and 1px border) to match the
        // plugin's surfaces by overriding the chrome resource keys in the window's own scope.
        // This reuses the existing plugin brushes rather than introducing chrome-specific tokens.
        public static void ApplyWindowThemeBrushes(Window window)
        {
            var app = Application.Current;
            if (window == null || app == null)
            {
                return;
            }

            // Back standalone popout windows with the opaque popout (popup/dialog) surface rather
            // than WindowSurface. WindowSurface ships transparent so embedded theme views blend into
            // the host; a floating window with a transparent backdrop would be see-through, so use
            // PopupSurface (guaranteed opaque via EnsureOpaqueIfRequired) instead.
            var popoutSurface =
                app.TryFindResource("PlayAch.Brush.Dialog.Background") as Brush ??
                app.TryFindResource("PlayAch.Brush.PopupSurface") as Brush ??
                app.TryFindResource("PlayAch.Brush.Window.Background") as Brush ??
                app.TryFindResource("PlayAch.Brush.WindowSurface") as Brush;
            if (popoutSurface != null)
            {
                window.Resources["WindowBackgourndBrush"] = popoutSurface;
                window.Resources["StandardWindowBackgroundBrush"] = popoutSurface;
                window.Resources["WindowBaseBackgroundBrush"] = popoutSurface;
                window.Background = popoutSurface;
            }

            var borderBrush =
                app.TryFindResource("PlayAch.Brush.Dialog.Border") as Brush ??
                app.TryFindResource("PlayAch.Brush.PopupBorder") as Brush ??
                app.TryFindResource("PlayAch.Brush.ControlBorder") as Brush;
            if (borderBrush != null)
            {
                window.Resources["PopupBorderBrush"] = borderBrush;
                window.Resources["NormalBorderBrush"] = borderBrush;
                window.Resources["StandardWindowBorderBrush"] = borderBrush;
            }

            if (app.TryFindResource("PlayAch.Brush.PopupSurface") is Brush popupSurface)
            {
                window.Resources["PopupBackgroundBrush"] = popupSurface;
            }

            if (app.TryFindResource("PlayAch.Brush.Text") is Brush textBrush)
            {
                window.Resources["TextBrush"] = textBrush;
            }

            if (app.TryFindResource("PlayAch.Brush.Text.Secondary") is Brush secondaryTextBrush)
            {
                window.Resources["TextBrushDarker"] = secondaryTextBrush;
            }

            if (app.TryFindResource("PlayAch.Brush.Text.Tertiary") is Brush tertiaryTextBrush)
            {
                window.Resources["TextBrushDark"] = tertiaryTextBrush;
            }

            if (app.TryFindResource("PlayAch.Brush.Glyph") is Brush glyphBrush)
            {
                window.Resources["GlyphBrush"] = glyphBrush;
            }

            if (app.TryFindResource("PlayAch.Brush.Accent") is Brush accentBrush)
            {
                window.Resources["HighlightGlyphBrush"] = accentBrush;
            }
        }

        private static Window CreateFullscreenWindow(string title, UserControl content, WindowOptions windowOptions)
        {
            var fsOptions = new WindowCreationOptions
            {
                ShowMinimizeButton = false,
                ShowMaximizeButton = false,
                ShowCloseButton = false
            };

            Window window = API.Instance.Dialogs.CreateWindow(fsOptions);

            FormattingCulture.Apply(window);
            window.Title = title;
            window.Tag = FullscreenWindowTag;
            ConfigureBorderlessFullscreenWindow(window);
            ApplyWindowThemeBrushes(window);

            var parent = API.Instance.Dialogs.GetCurrentAppWindow();
            ApplyFullscreenWindowPlacement(window, parent);

            if (content is RefreshProgressControl)
            {
                window.Content = new FullscreenOverlayContainer(
                    string.Empty,
                    content,
                    FullscreenSizeMode.Dialog);
            }
            else
            {
                content.HorizontalAlignment = HorizontalAlignment.Stretch;
                content.VerticalAlignment = VerticalAlignment.Stretch;
                window.Content = content;
            }

            window.PreviewKeyDown += new KeyEventHandler(HandleEsc);

            return window;
        }

        public static Window CreateBorderlessFullscreenWindow(IPlayniteAPI api, string title)
        {
            api = api ?? API.Instance;

            var window = api?.Dialogs?.CreateWindow(new WindowCreationOptions
            {
                ShowMinimizeButton = false,
                ShowMaximizeButton = false,
                ShowCloseButton = false
            }) ?? new Window();

            FormattingCulture.Apply(window);
            window.Title = title ?? string.Empty;
            ConfigureBorderlessFullscreenWindow(window);
            ApplyWindowThemeBrushes(window);
            ApplyFullscreenWindowPlacement(window, api?.Dialogs?.GetCurrentAppWindow());
            return window;
        }

        /// <summary>
        /// Sizes and positions a manual-placement window to cover the entire monitor the
        /// reference window is on (physical monitor bounds converted to DIPs via the reference's
        /// presentation source). Returns the monitor's physical pixel bounds, or null when
        /// placement failed and the window was left untouched.
        /// </summary>
        public static System.Drawing.Rectangle? PlaceOnWindowMonitor(Window window, Window reference)
        {
            if (window == null)
            {
                return null;
            }

            try
            {
                var handle = reference != null
                    ? new System.Windows.Interop.WindowInteropHelper(reference).Handle
                    : IntPtr.Zero;
                var screen = handle != IntPtr.Zero
                    ? System.Windows.Forms.Screen.FromHandle(handle)
                    : System.Windows.Forms.Screen.PrimaryScreen;
                var bounds = screen?.Bounds;
                if (bounds == null || bounds.Value.Width <= 0 || bounds.Value.Height <= 0)
                {
                    return null;
                }

                var toDips = Matrix.Identity;
                var source = reference != null ? PresentationSource.FromVisual(reference) : null;
                if (source?.CompositionTarget != null)
                {
                    toDips = source.CompositionTarget.TransformFromDevice;
                }

                var topLeft = toDips.Transform(new Point(bounds.Value.Left, bounds.Value.Top));
                var size = toDips.Transform(new Point(bounds.Value.Width, bounds.Value.Height));
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = topLeft.X;
                window.Top = topLeft.Y;
                window.Width = size.X;
                window.Height = size.Y;
                return bounds.Value;
            }
            catch
            {
                return null;
            }
        }

        public static Window CreateBorderlessTopmostWindow(IPlayniteAPI api, string title)
        {
            api = api ?? API.Instance;

            var window = api?.Dialogs?.CreateWindow(new WindowCreationOptions
            {
                ShowMinimizeButton = false,
                ShowMaximizeButton = false,
                ShowCloseButton = false
            }) ?? new Window();

            FormattingCulture.Apply(window);
            window.Title = title ?? string.Empty;
            window.ShowInTaskbar = false;
            window.ShowActivated = false;
            window.Focusable = false;
            window.Topmost = true;
            window.WindowStyle = WindowStyle.None;
            window.ResizeMode = ResizeMode.NoResize;
            window.SizeToContent = SizeToContent.WidthAndHeight;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.AllowsTransparency = true;
            window.Background = Brushes.Transparent;
            window.UseLayoutRounding = true;
            window.SnapsToDevicePixels = true;

            WindowChrome.SetWindowChrome(window, new WindowChrome
            {
                CaptionHeight = 0,
                GlassFrameThickness = new Thickness(0),
                ResizeBorderThickness = new Thickness(0),
                UseAeroCaptionButtons = false
            });

            window.Template = CreateContentOnlyWindowTemplate();
            return window;
        }

        private static void ConfigureBorderlessFullscreenWindow(Window window)
        {
            if (window == null)
            {
                return;
            }

            window.ShowInTaskbar = false;
            window.WindowStyle = WindowStyle.None;
            window.ResizeMode = ResizeMode.NoResize;
            window.SizeToContent = SizeToContent.Manual;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            window.UseLayoutRounding = true;
            window.SnapsToDevicePixels = true;

            WindowChrome.SetWindowChrome(window, new WindowChrome
            {
                CaptionHeight = 0,
                GlassFrameThickness = new Thickness(0),
                ResizeBorderThickness = new Thickness(0),
                UseAeroCaptionButtons = false
            });

            window.Template = CreateContentOnlyWindowTemplate();
        }

        private static void ApplyFullscreenWindowPlacement(Window window, Window parent)
        {
            if (window == null)
            {
                return;
            }

            if (parent != null)
            {
                window.Owner = parent;
            }

            window.Height = parent != null && parent.Height > 0
                ? parent.Height
                : SystemParameters.PrimaryScreenHeight;
            window.Width = parent != null && parent.Width > 0
                ? parent.Width
                : SystemParameters.PrimaryScreenWidth;
        }

        private static ControlTemplate CreateContentOnlyWindowTemplate()
        {
            var surface = new FrameworkElementFactory(typeof(Border));
            surface.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Window.BackgroundProperty));

            var adorner = new FrameworkElementFactory(typeof(AdornerDecorator));
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            adorner.AppendChild(presenter);
            surface.AppendChild(adorner);

            return new ControlTemplate(typeof(Window))
            {
                VisualTree = surface
            };
        }
    }

    public class WindowOptions : WindowCreationOptions
    {
        public double Width { get; set; }
        public double Height { get; set; }
        public bool CanBeResizable { get; set; } = false;
    }
}
