using System;
using System.Collections.Generic;
using System.Linq;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.ViewModels.Showcase.Widgets
{
    /// <summary>
    /// The Screenshot Slideshow's control bar: the achievement filters over the achievements its
    /// screenshots were taken for, and the screenshot style, one at a time.
    /// </summary>
    public sealed class SlideshowControlBarAdapter : CrossGameAchievementControlBarAdapter
    {
        // The styles in menu order with their labels, the capture settings' own.
        private static readonly (ShowcaseScreenshotVariant Variant, string Key)[] VariantChoices =
        {
            (ShowcaseScreenshotVariant.All, "LOCPlayAch_Common_All"),
            (ShowcaseScreenshotVariant.Clean, "LOCPlayAch_Settings_ScreenshotVariantClean"),
            (ShowcaseScreenshotVariant.Notification, "LOCPlayAch_Settings_Style_ToastTab"),
            (ShowcaseScreenshotVariant.Framed, "LOCPlayAch_Settings_ScreenshotVariantFramed")
        };

        private ShowcaseScreenshotVariant _variant = ShowcaseScreenshotVariant.All;

        public SlideshowControlBarAdapter()
        {
            ControlBar.Items.Add(new GridMultiSelectFilter(
                this,
                nameof(SelectedVariantText),
                () => SelectedVariantText,
                VariantChoices.Select(choice => choice.Variant.ToString()).ToList(),
                IsVariantSelected,
                SetVariantSelected,
                key => VariantLabel(ParseVariant(key)),
                hasAvailableAction: () => true)
            {
                Id = ShowcaseControlBarItems.ScreenshotStyle,
                LabelKey = "LOCPlayAch_Showcase_Variant",
                AutoHideWhenUnavailable = false,
                Width = 150
            });
        }

        /// <summary>Which captures play: one style, or every style.</summary>
        public ShowcaseScreenshotVariant Variant => _variant;

        public string SelectedVariantText => VariantLabel(_variant);

        public override ControlBarFilterState CaptureState()
        {
            var state = base.CaptureState();
            state.Variant = _variant == ShowcaseScreenshotVariant.All ? null : _variant.ToString();
            return state;
        }

        public override void RestoreState(ControlBarFilterState state)
        {
            base.RestoreState(state);
            _variant = ParseVariant(state?.Variant);
            OnPropertyChanged(nameof(SelectedVariantText));
        }

        private bool IsVariantSelected(string key) => ParseVariant(key) == _variant;

        // One style at a time: picking one replaces the last, and unpicking it plays every style.
        private void SetVariantSelected(string key, bool selected)
        {
            var picked = selected ? ParseVariant(key) : ShowcaseScreenshotVariant.All;
            if (picked == _variant)
            {
                return;
            }

            _variant = picked;
            OnPropertyChanged(nameof(SelectedVariantText));
            RaiseFilterChanged();
        }

        private static ShowcaseScreenshotVariant ParseVariant(string key) =>
            Enum.TryParse(key?.Trim(), true, out ShowcaseScreenshotVariant variant) &&
            Enum.IsDefined(typeof(ShowcaseScreenshotVariant), variant)
                ? variant
                : ShowcaseScreenshotVariant.All;

        private static string VariantLabel(ShowcaseScreenshotVariant variant) =>
            ResourceProvider.GetString(VariantChoices.First(choice => choice.Variant == variant).Key);
    }
}
