using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Playnite.SDK;

namespace PlayniteAchievements.Views.Helpers
{
    public static class PlayniteUiProvider
    {
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

            return windowExtension;
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

            window.Title = title;
            window.ShowInTaskbar = false;
            window.WindowStyle = WindowStyle.None;
            window.ResizeMode = ResizeMode.NoResize;

            var parent = API.Instance.Dialogs.GetCurrentAppWindow();
            if (parent != null)
            {
                window.Owner = parent;
            }
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;

            window.Height = parent != null && parent.Height > 0 ? parent.Height : SystemParameters.PrimaryScreenHeight;
            window.Width = parent != null && parent.Width > 0 ? parent.Width : SystemParameters.PrimaryScreenWidth;

            // Merge fullscreen resource fallbacks so desktop-only DynamicResource keys resolve.
            window.Resources.MergedDictionaries.Add(
                new ResourceDictionary
                {
                    Source = new Uri("/PlayniteAchievements;component/Resources/FullscreenResources.xaml", UriKind.Relative)
                });

            // Determine sizing mode based on the content type.
            var sizeMode = content is RefreshProgressControl
                ? FullscreenSizeMode.Dialog
                : FullscreenSizeMode.Fullscreen;

            var overlay = new FullscreenOverlayContainer(title, content, sizeMode);
            window.Content = overlay;

            window.PreviewKeyDown += new KeyEventHandler(HandleEsc);

            return window;
        }
    }

    public class WindowOptions : WindowCreationOptions
    {
        public double Width { get; set; }
        public double Height { get; set; }
        public bool CanBeResizable { get; set; } = false;
    }
}
