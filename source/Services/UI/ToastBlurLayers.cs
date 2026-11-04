using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using PlayniteAchievements.ViewModels;

namespace PlayniteAchievements.Services.UI
{
    /// <summary>
    /// The named layers of a blurred toast card that renders for saved outputs (screenshots and clip
    /// tracks) leave out. Those outputs blur their own frame under the card, so their card pixels
    /// must carry only the tint and the content: the live capture would bake one moment's blur into
    /// every clip frame, and the opaque card surface would hide the output's own blur.
    ///
    /// The card surface stays in only for the glow difference render, where it is what casts the
    /// glow's full shape (see the template's CardSurface comment).
    /// </summary>
    internal static class ToastBlurLayers
    {
        /// <summary>The bundled template's live blurred background image.</summary>
        public const string LiveImageName = "LiveBlurImage";

        /// <summary>The bundled template's opaque card surface (the glow caster).</summary>
        public const string CardSurfaceName = "CardSurface";

        /// <summary>
        /// Hides the blur layers of <paramref name="container"/>'s card for a synchronous render, and
        /// puts them back on dispose. A no-op for a card without a blurred background or a template
        /// without the named layers. Uses SetCurrentValue on both sides, as the effect stripping
        /// does, so the template's bindings and triggers keep ownership.
        /// </summary>
        public static IDisposable HideForOutput(FrameworkElement container, bool keepCardSurface)
        {
            if (!(container is ContentPresenter presenter) ||
                !(presenter.Content is AchievementToastViewModel vm) ||
                !vm.HasToastBlur ||
                presenter.ContentTemplate == null)
            {
                return Restore.None;
            }

            var hidden = new List<KeyValuePair<UIElement, Visibility>>(2);
            Hide(presenter, LiveImageName, hidden);
            if (!keepCardSurface)
            {
                Hide(presenter, CardSurfaceName, hidden);
            }

            return hidden.Count == 0 ? Restore.None : new Restore(hidden);
        }

        private static void Hide(ContentPresenter presenter, string name, List<KeyValuePair<UIElement, Visibility>> hidden)
        {
            if (presenter.ContentTemplate.FindName(name, presenter) is UIElement element &&
                element.Visibility == Visibility.Visible)
            {
                hidden.Add(new KeyValuePair<UIElement, Visibility>(element, element.Visibility));
                element.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Hidden);
            }
        }

        private sealed class Restore : IDisposable
        {
            public static readonly IDisposable None = new Restore(null);

            private readonly List<KeyValuePair<UIElement, Visibility>> _hidden;

            public Restore(List<KeyValuePair<UIElement, Visibility>> hidden)
            {
                _hidden = hidden;
            }

            public void Dispose()
            {
                if (_hidden == null)
                {
                    return;
                }

                foreach (var pair in _hidden)
                {
                    pair.Key.SetCurrentValue(UIElement.VisibilityProperty, pair.Value);
                }

                _hidden.Clear();
            }
        }
    }
}
