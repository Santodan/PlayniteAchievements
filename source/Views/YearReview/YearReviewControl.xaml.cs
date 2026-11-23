using System;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.ViewModels.YearReview;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Views.YearReview
{
    /// <summary>
    /// The Year in Review window: one slide at a time, stepped with a click, the arrow keys, the
    /// mouse wheel or the arrow buttons. Each slide plays its entrance when it is shown.
    /// </summary>
    public partial class YearReviewControl : UserControl
    {
        private const double TransitionMs = 350;
        private const double TransitionOffset = 60;
        private const double WheelStepMs = 450;
        private const double SavedImageWidth = 1920;
        private const double BackdropOpacity = 0.4;

        private readonly YearReviewViewModel _viewModel;
        private readonly Func<string> _pickSavePath;
        private readonly ILogger _logger;
        private int _shownIndex = -1;
        private DateTime _wheelReadyAt = DateTime.MinValue;

        internal YearReviewControl(YearReviewViewModel viewModel, Func<string> pickSavePath, ILogger logger)
        {
            _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            _pickSavePath = pickSavePath;
            _logger = logger;

            InitializeComponent();
            DataContext = _viewModel;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            Loaded += OnLoaded;
            PreviewKeyDown += OnPreviewKeyDown;
            PreviewMouseWheel += OnPreviewMouseWheel;
        }

        public string WindowTitle => ResourceProvider.GetString("LOCPlayAch_YearReview_Title");

        public void Cleanup()
        {
            Loaded -= OnLoaded;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            CurrentHost.Content = null;
            OutgoingHost.Content = null;
            _viewModel.Dispose();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Loaded -= OnLoaded;
            FormattingCulture.Apply(this);
            Focus();
            _ = _viewModel.LoadAsync();
        }

        private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(YearReviewViewModel.Slides))
            {
                // A new year starts over without sliding the old year's slide away.
                _shownIndex = -1;
                OutgoingHost.Content = null;
                CurrentHost.Content = null;
            }
            else if (e.PropertyName == nameof(YearReviewViewModel.CurrentSlide))
            {
                ShowCurrentSlide();
            }
        }

        private void ShowCurrentSlide()
        {
            var slide = _viewModel.CurrentSlide;
            var index = slide == null ? -1 : IndexOf(slide);
            var direction = index >= _shownIndex ? 1 : -1;
            var hadSlide = CurrentHost.Content != null;
            _shownIndex = index;

            OutgoingHost.BeginAnimation(OpacityProperty, null);
            OutgoingHost.Content = hadSlide ? CurrentHost.Content : null;
            CurrentHost.Content = slide;

            PlayBackdrop();

            if (YearReviewMotion.Enabled && hadSlide)
            {
                Slide(OutgoingHost, 1, 0, 0, -TransitionOffset * direction, () => OutgoingHost.Content = null);
                Slide(CurrentHost, 0, 1, TransitionOffset * direction, 0, null);
            }
            else
            {
                OutgoingHost.Content = null;
            }

            // The template's elements exist once the content has been laid out.
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (ReferenceEquals(CurrentHost.Content, slide))
                {
                    YearReviewMotion.Play(CurrentHost);
                }
            }));
        }

        /// <summary>Fades the new slide's art in and lets it settle from a slight zoom.</summary>
        private void PlayBackdrop()
        {
            if (!YearReviewMotion.Enabled)
            {
                return;
            }

            if (!(Backdrop.RenderTransform is ScaleTransform scale) || scale.IsFrozen)
            {
                scale = new ScaleTransform();
                Backdrop.RenderTransform = scale;
            }

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var fade = AnimationFrameRate.Apply(new DoubleAnimation(0, BackdropOpacity, TimeSpan.FromMilliseconds(700)) { FillBehavior = FillBehavior.Stop });
            var settle = AnimationFrameRate.Apply(new DoubleAnimation(1.12, 1, TimeSpan.FromSeconds(6)) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
            Backdrop.BeginAnimation(OpacityProperty, fade);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, settle);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, settle);
        }

        private int IndexOf(YearReviewSlide slide)
        {
            var slides = _viewModel.Slides;
            for (var i = 0; i < slides.Count; i++)
            {
                if (ReferenceEquals(slides[i], slide))
                {
                    return i;
                }
            }

            return -1;
        }

        private static void Slide(UIElement host, double fromOpacity, double toOpacity, double fromX, double toX, Action completed)
        {
            if (!(host.RenderTransform is TranslateTransform translate) || translate.IsFrozen)
            {
                translate = new TranslateTransform();
                host.RenderTransform = translate;
            }

            var ease = new SineEase { EasingMode = EasingMode.EaseOut };
            var duration = TimeSpan.FromMilliseconds(TransitionMs);
            var fade = AnimationFrameRate.Apply(new DoubleAnimation(fromOpacity, toOpacity, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
            var move = AnimationFrameRate.Apply(new DoubleAnimation(fromX, toX, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
            if (completed != null)
            {
                fade.Completed += (s, e) => completed();
            }

            host.BeginAnimation(OpacityProperty, fade);
            translate.BeginAnimation(TranslateTransform.XProperty, move);
        }

        private void Next() => _viewModel.Go(_shownIndex + 1);

        private void Previous() => _viewModel.Go(_shownIndex - 1);

        private void Next_Click(object sender, RoutedEventArgs e) => Next();

        private void Previous_Click(object sender, RoutedEventArgs e) => Previous();

        private void Deck_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (IsInside<ButtonBase>(e.OriginalSource as DependencyObject))
            {
                return;
            }

            Next();
            Focus();
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (IsInside<ComboBox>(e.OriginalSource as DependencyObject))
            {
                return;
            }

            switch (e.Key)
            {
                case Key.Right:
                case Key.PageDown:
                case Key.Space:
                    Next();
                    e.Handled = true;
                    break;
                case Key.Left:
                case Key.PageUp:
                    Previous();
                    e.Handled = true;
                    break;
                case Key.Home:
                    _viewModel.Go(0);
                    e.Handled = true;
                    break;
                case Key.End:
                    _viewModel.Go(_viewModel.Slides.Count - 1);
                    e.Handled = true;
                    break;
            }
        }

        /// <summary>One wheel step moves one slide; steps that arrive during the move are dropped.</summary>
        private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (IsInside<ComboBox>(e.OriginalSource as DependencyObject))
            {
                return;
            }

            e.Handled = true;
            var now = DateTime.UtcNow;
            if (now < _wheelReadyAt)
            {
                return;
            }

            _wheelReadyAt = now.AddMilliseconds(WheelStepMs);
            if (e.Delta < 0)
            {
                Next();
            }
            else
            {
                Previous();
            }
        }

        private static bool IsInside<T>(DependencyObject source) where T : DependencyObject
        {
            for (var current = source; current != null; current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            {
                if (current is T)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Saves the slide on screen, backdrop included, as a PNG. Its entrances are finished
        /// first, so the image shows the slide complete.
        /// </summary>
        private void SaveImage_Click(object sender, RoutedEventArgs e)
        {
            var stage = SlideStage;
            if (CurrentHost.Content == null || stage.ActualWidth <= 0 || stage.ActualHeight <= 0)
            {
                return;
            }

            var path = _pickSavePath?.Invoke();
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            YearReviewMotion.Reset(CurrentHost);
            CurrentHost.BeginAnimation(OpacityProperty, null);
            (CurrentHost.RenderTransform as TranslateTransform)?.BeginAnimation(TranslateTransform.XProperty, null);
            OutgoingHost.Content = null;
            Backdrop.BeginAnimation(OpacityProperty, null);
            if (Backdrop.RenderTransform is ScaleTransform zoom)
            {
                zoom.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                zoom.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            }

            stage.UpdateLayout();
            var card = stage;
            try
            {
                var scale = SavedImageWidth / card.ActualWidth;
                var width = (int)Math.Round(card.ActualWidth * scale);
                var height = (int)Math.Round(card.ActualHeight * scale);
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    dc.PushTransform(new ScaleTransform(scale, scale));
                    dc.DrawRectangle(new VisualBrush(card), null, new Rect(0, 0, card.ActualWidth, card.ActualHeight));
                }

                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var stream = File.Create(path))
                {
                    encoder.Save(stream);
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Year in Review: the slide image could not be saved.");
            }
        }
    }
}
