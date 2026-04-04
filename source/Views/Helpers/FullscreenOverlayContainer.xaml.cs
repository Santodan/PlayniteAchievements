using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PlayniteAchievements.Views.Helpers
{
    public enum FullscreenSizeMode
    {
        Fullscreen,
        Dialog
    }

    public partial class FullscreenOverlayContainer : UserControl
    {
        public static readonly DependencyProperty TitleProperty =
            DependencyProperty.Register(nameof(Title), typeof(string), typeof(FullscreenOverlayContainer),
                new PropertyMetadata(string.Empty, OnTitleChanged));

        public string Title
        {
            get => (string)GetValue(TitleProperty);
            set => SetValue(TitleProperty, value);
        }

        public FullscreenSizeMode SizeMode { get; set; } = FullscreenSizeMode.Fullscreen;

        public FrameworkElement HostedContent
        {
            get => ContentHost.Content as FrameworkElement;
            set => ContentHost.Content = value;
        }

        public FullscreenOverlayContainer()
        {
            InitializeComponent();
            CloseButton.Click += CloseButton_Click;
            Loaded += OnLoaded;
        }

        public FullscreenOverlayContainer(string title, FrameworkElement content, FullscreenSizeMode sizeMode)
            : this()
        {
            Title = title;
            SizeMode = sizeMode;
            HostedContent = content;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            ApplyFixedSize();
            FocusInitialElement();
        }

        private void ApplyFixedSize()
        {
            var parent = Window.GetWindow(this);
            double availWidth = parent?.ActualWidth > 0 ? parent.ActualWidth : SystemParameters.PrimaryScreenWidth;
            double availHeight = parent?.ActualHeight > 0 ? parent.ActualHeight : SystemParameters.PrimaryScreenHeight;

            if (SizeMode == FullscreenSizeMode.Dialog)
            {
                ContentPanel.Width = Math.Min(640, availWidth * 0.85);
                ContentPanel.Height = Math.Min(400, availHeight * 0.85);
            }
            else
            {
                ContentPanel.Width = availWidth * 0.92;
                ContentPanel.Height = availHeight * 0.92;
            }
        }

        private void FocusInitialElement()
        {
            if (HostedContent == null)
            {
                return;
            }

            var focusTarget = FindFirstFocusable(HostedContent);
            if (focusTarget != null)
            {
                FocusManager.SetFocusedElement(this, focusTarget);
                Keyboard.Focus(focusTarget);
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Window.GetWindow(this)?.Close();
        }

        private static void OnTitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var container = (FullscreenOverlayContainer)d;
            container.TitleText.Text = e.NewValue as string ?? string.Empty;
        }

        private static UIElement FindFirstFocusable(DependencyObject root)
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is UIElement element && element.Focusable && element.IsEnabled)
                {
                    return element;
                }

                var nested = FindFirstFocusable(child);
                if (nested != null)
                {
                    return nested;
                }
            }

            return null;
        }
    }
}
