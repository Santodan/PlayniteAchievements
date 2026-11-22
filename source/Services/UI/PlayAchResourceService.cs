using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Services.UI
{
    internal static class PlayAchResourceService
    {
        internal const string TransparentValue = ResourceOverrideSetting.TransparentValue;


        private sealed class TokenDefinition
        {
            public TokenDefinition(
                string resourceKey,
                string displayName,
                ResourceOverrideValueKind kind,
                string playniteResourceKey,
                bool isUserVisible,
                bool allowsOverride,
                params string[] fallbackPlayniteResourceKeys)
            {
                ResourceKey = resourceKey;
                DisplayName = displayName;
                Kind = kind;
                PlayniteResourceKey = playniteResourceKey;
                IsUserVisible = isUserVisible;
                AllowsOverride = allowsOverride;
                FallbackPlayniteResourceKeys = fallbackPlayniteResourceKeys ?? Array.Empty<string>();
            }

            public string ResourceKey { get; }
            public string DisplayName { get; }
            public ResourceOverrideValueKind Kind { get; }
            public string PlayniteResourceKey { get; }
            public bool IsUserVisible { get; }
            public bool AllowsOverride { get; }
            public IReadOnlyList<string> FallbackPlayniteResourceKeys { get; }
        }

        private sealed class AliasDefinition
        {
            public AliasDefinition(string resourceKey, string sourceResourceKey, byte? alpha)
            {
                ResourceKey = resourceKey;
                SourceResourceKey = sourceResourceKey;
                Alpha = alpha;
            }

            public string ResourceKey { get; }
            public string SourceResourceKey { get; }
            public byte? Alpha { get; }
        }

        // DisplayName holds a localization key (LOCPlayAch_*) resolved for display by the
        // settings view model. The non-user-visible Icon token is never shown, so it keeps a
        // plain internal label rather than a localized key.
        private static readonly TokenDefinition[] Tokens =
        {
            Brush("PlayAch.Brush.Text", "LOCPlayAch_Settings_Appearance_Resource_Text", "TextBrush"),
            Brush("PlayAch.Brush.Text.Secondary", "LOCPlayAch_Settings_Appearance_Resource_TextSecondary", "TextBrushDarker", "TextBrush"),
            // TextBrushDark is pure black in Playnite's default theme (it is the brush for text on light accents), so a muted-text token must not fall to it.
            Brush("PlayAch.Brush.Text.Tertiary", "LOCPlayAch_Settings_Appearance_Resource_TextTertiary", "TextBrushDarker", "TextBrush"),
            // Inputs: the brush each theme's own TextBox fills with (Helium names it
            // TextBlockBackgroundBrush; the default theme and most others use ControlBackgroundBrush).
            Brush("PlayAch.Brush.Surface", "LOCPlayAch_Settings_Appearance_Resource_Surface", "TextBlockBackgroundBrush", "ControlBackgroundBrush"),
            // Cards and containers: a theme's panel or card brush, else transparent so containers
            // never take the input fill.
            Brush("PlayAch.Brush.Panel", "LOCPlayAch_Settings_Appearance_Resource_Panel", "PanelBackgroundBrush", "CardBackgroundBrush"),
            Brush("PlayAch.Brush.WindowSurface", "LOCPlayAch_Settings_Appearance_Resource_WindowSurface", "WindowBackgourndBrush", "ControlBackgroundBrush"),
            Brush("PlayAch.Brush.GridSurface", "LOCPlayAch_Settings_Appearance_Resource_GridSurface", "GridItemBackgroundBrush", "ControlBackgroundBrush"),
            // Card outlines and dividers: the theme's panel separator. NormalBorderBrush is the
            // control border, which FusionX, Helium and others leave transparent.
            Brush("PlayAch.Brush.Border", "LOCPlayAch_Settings_Appearance_Resource_Border", "PanelSeparatorBrush", "NormalBorderBrush", "ControlBackgroundBrush"),
            Brush("PlayAch.Brush.ControlBorder", "LOCPlayAch_Settings_Appearance_Resource_ControlBorder", "NormalBrush", "SelectionLightBrush"),
            Brush("PlayAch.Brush.Glyph", "LOCPlayAch_ManageAchievements_Tab_CustomIcons", "GlyphBrush"),
            Brush("PlayAch.Brush.Accent", "LOCPlayAch_Settings_Appearance_Resource_Accent", "HighlightGlyphBrush", "GlyphBrush"),
            Brush("PlayAch.Brush.Selection", "LOCPlayAch_Settings_Appearance_Resource_Selection", "SelectionLightBrush", "GlyphBrush"),
            Brush("PlayAch.Brush.ControlSurface", "LOCPlayAch_Settings_Appearance_Resource_ControlSurface", "ButtonBackgroundBrush", "ControlBackgroundBrush"),
            Brush("PlayAch.Brush.PopupSurface", "LOCPlayAch_Settings_Appearance_Resource_PopupSurface", "PopupBackgroundBrush", "PanelBackgroundBrush", "ControlBackgroundBrush"),
            Brush("PlayAch.Brush.PopupBorder", "LOCPlayAch_Settings_Appearance_Resource_PopupBorder", "PopupBorderBrush", "NormalBorderBrush"),

            FontSize("PlayAch.FontSize.Caption", "LOCPlayAch_Settings_Appearance_Resource_CaptionSize", "FontSizeSmall"),
            FontSize("PlayAch.FontSize.Body", "LOCPlayAch_Settings_Appearance_Resource_BodySize", "FontSize"),
            FontSize("PlayAch.FontSize.Title", "LOCPlayAch_Settings_Appearance_Resource_TitleSize", "FontSizeLarge"),

            FontFamily("PlayAch.FontFamily.Body", "LOCPlayAch_Settings_Appearance_Resource_BodyFont", "FontFamily"),
            StaticFontFamily("PlayAch.FontFamily.Icon", "Icon font", "FontIcoFont")
        };

        private static readonly AliasDefinition[] Aliases =
        {
            Alias("PlayAch.Brush.Button.Background", "PlayAch.Brush.ControlSurface"),
            Alias("PlayAch.Brush.Button.Background.Hover", "PlayAch.Brush.Selection", 0x30),
            Alias("PlayAch.Brush.Button.Background.Selected", "PlayAch.Brush.Selection", 0x45),
            Alias("PlayAch.Brush.Button.Background.Pressed", "PlayAch.Brush.Selection", 0x60),
            Alias("PlayAch.Brush.Button.Border", "PlayAch.Brush.ControlBorder"),
            Alias("PlayAch.Brush.Control.Background", "PlayAch.Brush.ControlSurface"),
            Alias("PlayAch.Brush.Control.Background.Hover", "PlayAch.Brush.Selection", 0x30),
            Alias("PlayAch.Brush.Input.Background", "PlayAch.Brush.Surface"),
            Alias("PlayAch.Brush.Input.Border", "PlayAch.Brush.ControlBorder"),
            Alias("PlayAch.Brush.Grid.Background", "PlayAch.Brush.GridSurface"),
            Alias("PlayAch.Brush.Grid.HeaderBackground", "PlayAch.Brush.GridSurface"),
            Alias("PlayAch.Brush.Grid.HeaderGripper.Hover", "PlayAch.Brush.Accent"),
            Alias("PlayAch.Brush.Grid.HeaderGripper.Pressed", "PlayAch.Brush.Selection"),
            Alias("PlayAch.Brush.Grid.RowBackground", "PlayAch.Brush.GridSurface"),
            Alias("PlayAch.Brush.Grid.RowHoverBackground", "PlayAch.Brush.Selection", 0x30),
            Alias("PlayAch.Brush.Grid.RowSelectedBackground", "PlayAch.Brush.Selection", 0x45),
            Alias("PlayAch.Brush.Window.Background", "PlayAch.Brush.WindowSurface"),
            Alias("PlayAch.Brush.Dialog.Background", "PlayAch.Brush.PopupSurface"),
            Alias("PlayAch.Brush.Dialog.Border", "PlayAch.Brush.PopupBorder"),
            Alias("PlayAch.Brush.Chart.Separator", "PlayAch.Brush.ControlBorder"),
            Alias("PlayAch.Brush.Progress.Track", "PlayAch.Brush.Surface"),
            Alias("PlayAch.Brush.Progress.Fill", "PlayAch.Brush.Accent"),
            Alias("PlayAch.Brush.Progress.Border", "PlayAch.Brush.Accent"),
            Alias("PlayAch.Brush.Chrome.Background", "PlayAch.Brush.ControlSurface", 0x1F),
            Alias("PlayAch.Brush.ScrollBar.Track", "PlayAch.Brush.GridSurface", 0x00),
            Alias("PlayAch.Brush.ScrollBar.Thumb", "PlayAch.Brush.ControlBorder"),
            Alias("PlayAch.Brush.ScrollBar.Thumb.Hover", "PlayAch.Brush.Glyph"),
            Alias("PlayAch.Brush.ScrollBar.Thumb.Pressed", "PlayAch.Brush.Accent")
        };

        public static IReadOnlyList<ResourceOverrideDescriptor> ResourceDescriptors { get; } =
            Tokens
                .Where(token => token.IsUserVisible)
                .Select(token => new ResourceOverrideDescriptor(
                    token.ResourceKey,
                    token.DisplayName,
                    token.Kind,
                    token.PlayniteResourceKey,
                    token.FallbackPlayniteResourceKeys.ToArray()))
                .ToList()
                .AsReadOnly();

        /// <summary>Applies the tokens at application scope as one resource change.</summary>
        public static void ApplyToApplication(
            IDictionary<string, ResourceOverrideSetting> overrides,
            PersistedSettings settings = null)
        {
            PlayAchRuntimeResources.Update(resources => Apply(resources, overrides, settings));
        }

        public static void Apply(
            ResourceDictionary resources,
            IDictionary<string, ResourceOverrideSetting> overrides,
            PersistedSettings settings = null)
        {
            if (resources == null)
            {
                return;
            }

            foreach (var token in Tokens)
            {
                resources[token.ResourceKey] = ResolveToken(token, overrides);
            }

            foreach (var alias in Aliases)
            {
                resources[alias.ResourceKey] = ResolveAlias(resources, alias);
            }

            resources[WindowFrameResourceKey] = ResolveWindowFrame(resources);

            RarityAppearanceHelper.ApplyCompletedGameBrushResource(resources, settings);
            RarityAppearanceHelper.ApplyCompletedGlowEffectResources(resources, settings);
            RarityAppearanceHelper.ApplyCompletedProgressFillResource(resources, settings);
            ApplyIconScaleResource(resources, settings);
            ApplyCornerRoundingResources(resources, settings);
        }

        /// <summary>
        /// Resource key for whether column art stops at its own pixel size, consumed through
        /// AsyncImage.CapAtOwnSize. Left unwritten without settings, so a staged pass keeps the
        /// last value and an unresolved reference falls back to the property's false default.
        /// </summary>
        public const string CapAtOwnSizeResourceKey = "PlayAch.Image.CapAtOwnSize";

        /// <summary>Publishes the icon scale mode at application scope.</summary>
        public static void ApplyIconScaleToApplication(PersistedSettings settings)
        {
            if (settings == null)
            {
                return;
            }

            PlayAchRuntimeResources.Update(resources => ApplyIconScaleResource(resources, settings));
        }

        private static void ApplyIconScaleResource(ResourceDictionary resources, PersistedSettings settings)
        {
            if (resources == null || settings == null)
            {
                return;
            }

            resources[CapAtOwnSizeResourceKey] = settings.IconScaleMode == IconScaleMode.OwnSizeMax;
        }

        /// <summary>
        /// Resource keys for the corner radius (a double, in pixels) of game and category art and of
        /// achievement icons, consumed through RoundedClip.Radius. 0 when that kind is not rounded.
        /// Left unwritten without settings, so an unresolved reference falls back to 0.
        /// </summary>
        public const string ArtCornerRadiusResourceKey = "PlayAch.Radius.Art";
        public const string AchievementIconCornerRadiusResourceKey = "PlayAch.Radius.AchievementIcon";

        /// <summary>
        /// The same radii as CornerRadius values, for Borders drawn over the art (frames, scrims)
        /// so their corners follow the clipped picture.
        /// </summary>
        public const string ArtCornerRadiusShapeResourceKey = "PlayAch.CornerRadius.Art";
        public const string AchievementIconCornerRadiusShapeResourceKey = "PlayAch.CornerRadius.AchievementIcon";

        /// <summary>
        /// Resource keys for the corner radius of layout surfaces (inputs, buttons, cards, menus):
        /// all four corners, the left or right pair for the halves of a joined group, and the
        /// radius as a double for code that derives from it. All 0 when layout rounding is off.
        /// The keys exist only at runtime, so no merged static dictionary can hold one.
        /// </summary>
        public const string LayoutCornerRadiusResourceKey = "PlayAch.Radius.Layout";
        public const string LayoutCornerRadiusLeftResourceKey = "PlayAch.Radius.Layout.Left";
        public const string LayoutCornerRadiusRightResourceKey = "PlayAch.Radius.Layout.Right";
        public const string LayoutCornerRadiusValueResourceKey = "PlayAch.Radius.Layout.Value";

        /// <summary>Publishes the corner rounding settings at application scope.</summary>
        public static void ApplyCornerRoundingToApplication(PersistedSettings settings)
        {
            if (settings == null)
            {
                return;
            }

            PlayAchRuntimeResources.Update(resources => ApplyCornerRoundingResources(resources, settings));
        }

        /// <summary>The published radius for one kind of image: the shared radius when enabled, else 0.</summary>
        public static double ResolveCornerRadius(bool enabled, int radius) =>
            enabled ? Math.Max(0, radius) : 0d;

        private static void ApplyCornerRoundingResources(ResourceDictionary resources, PersistedSettings settings)
        {
            if (resources == null || settings == null)
            {
                return;
            }

            var art = ResolveCornerRadius(settings.RoundGameArtCorners, settings.CornerRoundingRadius);
            var icon = ResolveCornerRadius(settings.RoundAchievementIconCorners, settings.AchievementIconCornerRadius);
            resources[ArtCornerRadiusResourceKey] = art;
            resources[AchievementIconCornerRadiusResourceKey] = icon;
            resources[ArtCornerRadiusShapeResourceKey] = new CornerRadius(art);
            resources[AchievementIconCornerRadiusShapeResourceKey] = new CornerRadius(icon);

            var layout = ResolveCornerRadius(settings.RoundLayoutCorners, settings.LayoutCornerRadius);
            resources[LayoutCornerRadiusResourceKey] = new CornerRadius(layout);
            resources[LayoutCornerRadiusLeftResourceKey] = new CornerRadius(layout, 0, 0, layout);
            resources[LayoutCornerRadiusRightResourceKey] = new CornerRadius(0, layout, layout, 0);
            resources[LayoutCornerRadiusValueResourceKey] = layout;
        }

        private static TokenDefinition Brush(
            string resourceKey,
            string displayName,
            string playniteResourceKey,
            params string[] fallbackPlayniteResourceKeys)
        {
            return new TokenDefinition(
                resourceKey,
                displayName,
                ResourceOverrideValueKind.Brush,
                playniteResourceKey,
                true,
                true,
                fallbackPlayniteResourceKeys);
        }

        private static TokenDefinition FontSize(
            string resourceKey,
            string displayName,
            string playniteResourceKey)
        {
            return new TokenDefinition(
                resourceKey,
                displayName,
                ResourceOverrideValueKind.FontSize,
                playniteResourceKey,
                true,
                true);
        }

        private static AliasDefinition Alias(string resourceKey, string sourceResourceKey, byte? alpha = null)
        {
            return new AliasDefinition(resourceKey, sourceResourceKey, alpha);
        }

        private static TokenDefinition FontFamily(
            string resourceKey,
            string displayName,
            string playniteResourceKey)
        {
            return new TokenDefinition(
                resourceKey,
                displayName,
                ResourceOverrideValueKind.FontFamily,
                playniteResourceKey,
                true,
                true);
        }

        private static TokenDefinition StaticFontFamily(
            string resourceKey,
            string displayName,
            string playniteResourceKey)
        {
            return new TokenDefinition(
                resourceKey,
                displayName,
                ResourceOverrideValueKind.FontFamily,
                playniteResourceKey,
                false,
                false);
        }

        private static object ResolveToken(TokenDefinition token, IDictionary<string, ResourceOverrideSetting> overrides)
        {
            var setting = GetSetting(token.ResourceKey, overrides);
            if (token.AllowsOverride && setting != null)
            {
                if (setting.Mode == ResourceOverrideMode.Transparent &&
                    token.Kind == ResourceOverrideValueKind.Brush)
                {
                    return CreateBrush(TransparentValue);
                }

                if (setting.Mode == ResourceOverrideMode.Custom &&
                    TryParseCustomValue(token, setting.CustomValue, out var customValue))
                {
                    return customValue;
                }
            }

            var playniteValue = FindPlayniteResource(token);
            return EnsureOpaqueIfRequired(token, CoerceValue(token, playniteValue));
        }

        // Last-resort guarantee for popup/menu surfaces: if every brush in the fallback chain was
        // transparent, keep the theme's color but force it opaque so the floating surface is never
        // see-through. No-op for every other token.
        private static object EnsureOpaqueIfRequired(TokenDefinition token, object value)
        {
            if (!OpaqueRequiredTokens.Contains(token.ResourceKey) ||
                !(value is SolidColorBrush solid) ||
                solid.Color.A != 0)
            {
                return value;
            }

            var color = solid.Color;
            color.A = 255;
            return CreateBrush(color);
        }

        private static object FindPlayniteResource(TokenDefinition token)
        {
            var value = Application.Current?.TryFindResource(token.PlayniteResourceKey);
            if (IsUsableResource(token, value))
            {
                return value;
            }

            foreach (var fallbackKey in token.FallbackPlayniteResourceKeys)
            {
                value = Application.Current?.TryFindResource(fallbackKey);
                if (IsUsableResource(token, value))
                {
                    return value;
                }
            }

            if (value == null && TransparentWhenMissingTokens.Contains(token.ResourceKey))
            {
                return CreateBrush(TransparentValue);
            }

            return value;
        }

        // Tokens whose fallback chain ends in transparent rather than an unset resource, so a
        // container with no theme brush stays hit-testable and draws only its border.
        private static readonly HashSet<string> TransparentWhenMissingTokens = new HashSet<string>(StringComparer.Ordinal)
        {
            "PlayAch.Brush.Panel"
        };

        // Surfaces that float over other content (dropdown popups, context menus) must be opaque
        // to stay readable. Themes often define their panel/surface brushes as transparent so they
        // overlay the window background; that is fine for inline surfaces but unusable for a popup.
        // For these tokens only, treat a fully transparent brush as missing so the fallback chain
        // continues to a solid surface. Other tokens keep any transparency the theme intends.
        private static readonly HashSet<string> OpaqueRequiredTokens = new HashSet<string>(StringComparer.Ordinal)
        {
            "PlayAch.Brush.PopupSurface"
        };

        // Outlines that exist only to be seen: a transparent theme brush moves on to the next key in
        // the chain instead of hiding the edge.
        private static readonly HashSet<string> VisibleRequiredTokens = new HashSet<string>(StringComparer.Ordinal)
        {
            "PlayAch.Brush.Border"
        };

        private static bool IsUsableResource(TokenDefinition token, object value)
        {
            if (value == null)
            {
                return false;
            }

            if ((OpaqueRequiredTokens.Contains(token.ResourceKey) || VisibleRequiredTokens.Contains(token.ResourceKey)) &&
                IsFullyTransparent(value as Brush))
            {
                return false;
            }

            return true;
        }

        private static bool IsFullyTransparent(Brush brush)
        {
            if (brush == null)
            {
                return false;
            }

            if (brush.Opacity <= 0.0)
            {
                return true;
            }

            return brush is SolidColorBrush solid && solid.Color.A == 0;
        }

        /// <summary>
        /// Resource key for a popout window's own background, which Playnite's window template also
        /// paints behind the title bar. It is the Window background setting, so the title bar and
        /// the content match; a transparent setting uses the opaque popup surface instead, because a
        /// window has nothing behind it to show through.
        /// </summary>
        public const string WindowFrameResourceKey = "PlayAch.Brush.Window.Frame";

        private static object ResolveWindowFrame(ResourceDictionary resources)
        {
            var surface = resources["PlayAch.Brush.WindowSurface"] as Brush;
            if (surface != null && !IsFullyTransparent(surface))
            {
                return surface;
            }

            return resources["PlayAch.Brush.PopupSurface"] ?? surface;
        }

        private static object ResolveAlias(ResourceDictionary resources, AliasDefinition alias)
        {
            if (!resources.Contains(alias.SourceResourceKey))
            {
                return null;
            }

            var value = resources[alias.SourceResourceKey];
            if (!alias.Alpha.HasValue || !(value is Brush brush))
            {
                return value;
            }

            return CreateBrushWithAlpha(brush, alias.Alpha.Value);
        }

        private static Brush CreateBrushWithAlpha(Brush brush, byte alpha)
        {
            if (brush is SolidColorBrush solid)
            {
                var color = solid.Color;
                color.A = (byte)Math.Round(color.A * (alpha / 255.0));
                return CreateBrush(color);
            }

            var clone = brush.CloneCurrentValue();
            clone.Opacity = clone.Opacity * (alpha / 255.0);
            if (clone.CanFreeze)
            {
                clone.Freeze();
            }

            return clone;
        }

        private static ResourceOverrideSetting GetSetting(
            string resourceKey,
            IDictionary<string, ResourceOverrideSetting> overrides)
        {
            if (overrides == null || string.IsNullOrWhiteSpace(resourceKey))
            {
                return null;
            }

            return overrides.TryGetValue(resourceKey, out var setting) ? setting : null;
        }

        private static bool TryParseCustomValue(TokenDefinition token, string value, out object result)
        {
            result = null;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            switch (token.Kind)
            {
                case ResourceOverrideValueKind.Brush:
                    result = CreateBrush(value);
                    return result != null;

                case ResourceOverrideValueKind.FontSize:
                    if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var size) &&
                        size > 0 &&
                        !double.IsNaN(size) &&
                        !double.IsInfinity(size))
                    {
                        result = size;
                        return true;
                    }
                    return false;

                case ResourceOverrideValueKind.FontFamily:
                    result = new FontFamily(value.Trim());
                    return true;

                default:
                    return false;
            }
        }

        private static object CoerceValue(TokenDefinition token, object value)
        {
            if (value == null)
            {
                return null;
            }

            switch (token.Kind)
            {
                case ResourceOverrideValueKind.Brush:
                    return CoerceBrush(value);

                case ResourceOverrideValueKind.FontSize:
                    return CoerceFontSize(value);

                case ResourceOverrideValueKind.FontFamily:
                    return value is FontFamily ? value : new FontFamily(value.ToString());

                default:
                    return null;
            }
        }

        private static Brush CoerceBrush(object value)
        {
            if (value is Brush brush)
            {
                return brush;
            }

            if (value is Color color)
            {
                return CreateBrush(color);
            }

            return CreateBrush(value.ToString());
        }

        private static object CoerceFontSize(object value)
        {
            if (value is double number)
            {
                return number;
            }

            if (value is float floatValue)
            {
                return (double)floatValue;
            }

            if (value is int intValue)
            {
                return (double)intValue;
            }

            return double.TryParse(
                value.ToString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed)
                ? parsed
                : (object)null;
        }

        private static SolidColorBrush CreateBrush(string color)
        {
            try
            {
                return CreateBrush((Color)ColorConverter.ConvertFromString(color));
            }
            catch
            {
                return null;
            }
        }

        private static SolidColorBrush CreateBrush(Color color)
        {
            var brush = new SolidColorBrush(color);
            if (brush.CanFreeze)
            {
                brush.Freeze();
            }

            return brush;
        }
    }
}
