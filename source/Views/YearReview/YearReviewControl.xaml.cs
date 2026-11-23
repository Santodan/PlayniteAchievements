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

        /// <summary>Saves the element given as the parameter, the summary card, as a PNG.</summary>
        public static readonly RoutedUICommand SaveCardCommand = new RoutedUICommand("SaveCard", "SaveCard", typeof(YearReviewControl));

        private readonly YearReviewViewModel _viewModel;
        private readonly ILogger _logger;
        private int _shownIndex = -1;
        private DateTime _wheelReadyAt = DateTime.MinValue;

        internal YearReviewControl(YearReviewViewModel viewModel, ILogger logger)
        {
            _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            _logger = logger;

            InitializeComponent();
            DataContext = _viewModel;
            CommandBindings.Add(new CommandBinding(SaveCardCommand, OnSaveCard));
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            Loaded += OnLoaded;
            PreviewKeyDown += OnPreviewKeyDown;
            PreviewMouseWheel += OnPreviewMouseWheel;
        }

        public string WindowTitle => ResourceProvider.GetString("LOCPlayAch_YearReview_Title");

        public void Cleanup()
        {
            Loaded -= OnLoaded;
            YearReviewUnlockPopover.Close();
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
            YearReviewUnlockPopover.Close();
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

        /// <summary>Fades the new slide's art in.</summary>
        private void PlayBackdrop()
        {
            if (!YearReviewMotion.Enabled)
            {
                return;
            }

            var fade = AnimationFrameRate.Apply(new DoubleAnimation(0, BackdropOpacity, TimeSpan.FromMilliseconds(700)) { FillBehavior = FillBehavior.Stop });
            Backdrop.BeginAnimation(OpacityProperty, fade);
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

        /// <summary>
        /// A click on a month bar or a day opens its unlocks; a click on anything else that
        /// reacts to the mouse is left alone; a click on the slide itself moves to the next one.
        /// </summary>
        private void Deck_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            var source = e.OriginalSource as DependencyObject;
            if (IsInside<ButtonBase>(source))
            {
                return;
            }

            if (YearReviewUnlockPopover.FindTarget(source) is FrameworkElement target)
            {
                e.Handled = true;
                YearReviewUnlockPopover.Open(target);
                return;
            }

            if (IsHoverable(source))
            {
                return;
            }

            Next();
            Focus();
        }

        private bool IsHoverable(DependencyObject source)
        {
            for (var current = source; current != null && !ReferenceEquals(current, Deck); current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            {
                if (YearReviewMotion.GetHover(current))
                {
                    return true;
                }
            }

            return false;
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
            if (!(CurrentHost.Content is YearReviewSlide slide))
            {
                return;
            }

            var path = PickPath(FileName(slide.Title));
            if (path == null)
            {
                return;
            }

            FinishMotion();
            Save(SlideStage, path);
        }

        /// <summary>Saves the summary card alone.</summary>
        private void OnSaveCard(object sender, ExecutedRoutedEventArgs e)
        {
            if (!(e.Parameter is FrameworkElement card))
            {
                return;
            }

            var path = PickPath(FileName(null));
            if (path == null)
            {
                return;
            }

            FinishMotion();
            Save(card, path);
        }

        /// <summary>"Year in Review 2026 - Unlocks by month.png", or without the slide part for the card.</summary>
        private string FileName(string slideTitle)
        {
            var name = WindowTitle + " " + _viewModel.SelectedYear.ToString(FormattingCulture.Current);
            if (!string.IsNullOrWhiteSpace(slideTitle))
            {
                name += " - " + slideTitle;
            }

            foreach (var invalid in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(invalid, '_');
            }

            return name + ".png";
        }

        /// <summary>The save dialog, starting from <paramref name="suggested"/>; null when cancelled.</summary>
        private string PickPath(string suggested)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                FileName = suggested,
                DefaultExt = ".png",
                AddExtension = true,
                OverwritePrompt = true,
                Filter = ResourceProvider.GetString("LOCPlayAch_YearReview_PngImage") + "|*.png"
            };

            return dialog.ShowDialog(Window.GetWindow(this)) == true ? dialog.FileName : null;
        }

        /// <summary>Ends every running entrance and transition, so a capture shows each element at rest.</summary>
        private void FinishMotion()
        {
            YearReviewMotion.Reset(CurrentHost);
            CurrentHost.BeginAnimation(OpacityProperty, null);
            (CurrentHost.RenderTransform as TranslateTransform)?.BeginAnimation(TranslateTransform.XProperty, null);
            OutgoingHost.Content = null;
            Backdrop.BeginAnimation(OpacityProperty, null);
            SlideStage.UpdateLayout();
        }

        private void Save(FrameworkElement element, string path)
        {
            if (element.ActualWidth <= 0 || element.ActualHeight <= 0)
            {
                return;
            }

            try
            {
                var scale = SavedImageWidth / element.ActualWidth;
                var width = (int)Math.Round(element.ActualWidth * scale);
                var height = (int)Math.Round(element.ActualHeight * scale);
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    dc.PushTransform(new ScaleTransform(scale, scale));
                    dc.DrawRectangle(new VisualBrush(element), null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
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
                _logger?.Error(ex, "Year in Review: the image could not be saved.");
            }
        }
    }
}
