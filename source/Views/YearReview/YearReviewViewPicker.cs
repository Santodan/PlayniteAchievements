using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using PlayniteAchievements.ViewModels.YearReview;

namespace PlayniteAchievements.Views.YearReview
{
    /// <summary>
    /// The list of a slide's views, opened under the slide's picker button. Picking a view sets
    /// the slide's current view and closes the list.
    /// </summary>
    public static class YearReviewViewPicker
    {
        private const string TemplateKey = "YearReview.ViewPickerPopupTemplate";

        private static Popup _popup;
        private static YearReviewSlide _slide;

        public static void Open(FrameworkElement button)
        {
            if (!(button?.DataContext is YearReviewSlide slide) || !slide.HasViews)
            {
                return;
            }

            Close();
            YearReviewUnlockPopover.Close();
            _slide = slide;
            slide.PropertyChanged += OnSlidePropertyChanged;
            _popup = new Popup
            {
                PlacementTarget = button,
                Placement = PlacementMode.Bottom,
                StaysOpen = false,
                AllowsTransparency = true,
                PopupAnimation = PopupAnimation.Fade,
                Child = new ContentControl
                {
                    Content = slide,
                    ContentTemplate = button.TryFindResource(TemplateKey) as DataTemplate,
                    Focusable = false
                }
            };
            _popup.Closed += (s, e) =>
            {
                if (ReferenceEquals(s, _popup))
                {
                    Detach();
                    _popup = null;
                }
            };
            _popup.IsOpen = true;
        }

        private static void OnSlidePropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(YearReviewSlide.CurrentView))
            {
                // Closed after the list has finished applying the selection.
                var popup = _popup;
                popup?.Dispatcher.BeginInvoke(new System.Action(() =>
                {
                    if (ReferenceEquals(popup, _popup))
                    {
                        Close();
                    }
                }));
            }
        }

        private static void Detach()
        {
            if (_slide != null)
            {
                _slide.PropertyChanged -= OnSlidePropertyChanged;
                _slide = null;
            }
        }

        public static void Close()
        {
            Detach();
            var popup = _popup;
            _popup = null;
            if (popup != null)
            {
                popup.IsOpen = false;
                popup.Child = null;
            }
        }
    }
}
