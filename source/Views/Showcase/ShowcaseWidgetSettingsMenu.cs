using System;
using System.Windows;
using System.Windows.Controls;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Views.Showcase
{
    /// <summary>
    /// The right-click "Widget settings" entry every widget offers. A host that can edit its
    /// widgets (the Showcase page and strip, a start page view) sets <see cref="OpenerProperty"/>
    /// on itself; the value inherits down to every element inside its widgets, so any menu raised
    /// there, a grid row's included, can open the settings of the widget it sits in.
    /// </summary>
    internal static class ShowcaseWidgetSettingsMenu
    {
        private const string MenuItemKey = "LOCPlayAch_Showcase_WidgetSettings";

        public static readonly DependencyProperty OpenerProperty =
            DependencyProperty.RegisterAttached(
                "Opener",
                typeof(Action<string>),
                typeof(ShowcaseWidgetSettingsMenu),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits));

        /// <summary>Opens the settings of the widget instance whose ID it is given.</summary>
        public static void SetOpener(DependencyObject element, Action<string> value) =>
            element.SetValue(OpenerProperty, value);

        public static Action<string> GetOpener(DependencyObject element) =>
            (Action<string>)element.GetValue(OpenerProperty);

        /// <summary>
        /// The "Widget settings" item for the widget <paramref name="source"/> sits in, or null
        /// when it is in no widget or its host cannot edit widgets.
        /// </summary>
        public static MenuItem TryCreateItem(FrameworkElement resourceOwner, DependencyObject source)
        {
            var widget = source as ShowcaseWidgetControl ??
                VisualTreeHelpers.FindVisualParent<ShowcaseWidgetControl>(source);
            var instanceId = widget?.InstanceId;
            var opener = widget == null ? null : GetOpener(widget);
            if (opener == null || string.IsNullOrWhiteSpace(instanceId))
            {
                return null;
            }

            return GameRowContextMenuBuilder.CreateMenuItem(
                resourceOwner,
                MenuItemKey,
                () => opener(instanceId));
        }
    }
}
