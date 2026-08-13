using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace PlayniteAchievements.ViewModels
{
    /// <summary>
    /// One reorderable text line of a notification surface. The toast and frame templates
    /// render an ItemsControl over these, using WPF implicit DataTemplates keyed on the
    /// concrete descriptor type, so the user's line order is pure data. Values that differ
    /// between the two surfaces (font size, visibility flags, brushes) are resolved onto the
    /// descriptor when the owning view model builds its per-surface list; everything else is
    /// bound through <see cref="Parent"/>.
    /// </summary>
    public abstract class ToastLineDescriptor
    {
        protected ToastLineDescriptor(
            AchievementToastViewModel parent,
            double fontSize,
            FontFamily fontFamily,
            Effect textShadow)
        {
            Parent = parent;
            FontSize = fontSize;
            FontFamily = fontFamily;
            TextShadow = textShadow;
        }

        /// <summary>
        /// Second, tighter shadow layer nested under <see cref="TextShadow"/>; null at and
        /// below the built-in strength. Resolved per surface by the owning view model.
        /// </summary>
        public Effect TextShadowInner { get; set; }

        /// <summary>
        /// The surface's image shadow, for line elements that are artwork rather than text
        /// (the title line's inline rarity badge). Resolved per surface by the owning view
        /// model.
        /// </summary>
        public Effect ImageShadow { get; set; }

        /// <summary>
        /// Whole-line font weight from the surface's per-line emphasis setting. The title line
        /// defaults to SemiBold, every other line to Normal; the bold toggle raises either to
        /// Bold. Resolved by the owning view model.
        /// </summary>
        public FontWeight FontWeight { get; set; } = FontWeights.Normal;

        /// <summary>
        /// Whole-line font style (italic toggle). Resolved by the owning view model.
        /// </summary>
        public FontStyle FontStyle { get; set; } = FontStyles.Normal;

        /// <summary>
        /// Whole-line text decorations (underline/strikethrough toggles), or null for none.
        /// Inline markdown in the line text composes on top, except that a run carrying its
        /// own markdown decoration replaces the line decoration for that run.
        /// </summary>
        public TextDecorationCollection TextDecorations { get; set; }

        public AchievementToastViewModel Parent { get; }

        public double FontSize { get; }

        // Bound as a local value on each line TextBlock: the shared TextBlock base style sets
        // FontFamily via a style setter, which would beat an inherited TextElement.FontFamily.
        public FontFamily FontFamily { get; }

        /// <summary>
        /// The surface's content drop shadow (strength-scaled), applied on each line's root;
        /// null when the user disabled the shadow.
        /// </summary>
        public Effect TextShadow { get; }

        /// <summary>
        /// Horizontal left indent (DIPs) applied to this line through the ItemsControl item
        /// container. Drives the name-line offset: a positive offset indents the title line, a
        /// negative offset indents the remaining lines instead, so the title line (with its inline
        /// badge) never slides left under the icon column.
        /// </summary>
        public double LeftIndent { get; set; }

        /// <summary>
        /// Extra top and bottom padding (DIPs) added around this line, from the surface's line
        /// padding setting. Adds vertical breathing room between stacked text rows.
        /// </summary>
        public double VerticalPadding { get; set; }

        /// <summary>
        /// Whether this is the bottom-most visible line of its surface, set by the owning view
        /// model once the user's line order is resolved. Only that line needs
        /// <see cref="DescenderSlack"/>: a line with another line under it overhangs into the
        /// next line box's empty top leading, which nothing clips.
        /// </summary>
        public bool IsBottomLine { get; set; }

        /// <summary>
        /// Glyph ink can fall below a TextBlock's measured height, so a descender (p, q, g, y) on
        /// the bottom line renders outside the bounds that the line host's ClipToBounds, the
        /// description's <see cref="ToastDescriptionLine.MaxTextHeight"/> clamp, and the overlay
        /// capture's layout-bounds viewbox all cut at. This reserves room for that overhang.
        /// <para>
        /// Measured over 1584 cases -- 18 font families, 8 to 48 DIP, every weight/style
        /// combination, one and two lines -- the overhang never exceeds 1.026 DIP and does not
        /// scale with the font, being a sub-pixel baseline residual rather than the descent. A flat
        /// cap therefore covers it, and the font's own descent bounds it from the other side: no
        /// measured face reported a descent smaller than its overhang.
        /// </para>
        /// </summary>
        public double DescenderSlack =>
            IsBottomLine ? Math.Min(FontDescent, MaxDescenderOverhangDip) : 0;

        private const double MaxDescenderOverhangDip = 1.5d;

        /// <summary>
        /// The font's declared descent (DIPs) at this line's size. FontFamily.LineSpacing and
        /// .Baseline are both em-relative, so their difference is the descent in ems.
        /// </summary>
        public double FontDescent
        {
            get
            {
                var family = FontFamily;
                if (family == null)
                {
                    return 0;
                }

                var descent = family.LineSpacing - family.Baseline;
                return descent > 0 ? FontSize * descent : 0;
            }
        }

        /// <summary>
        /// The line's container margin: the <see cref="LeftIndent"/> on the left, the
        /// <see cref="VerticalPadding"/> on top and bottom, and <see cref="DescenderSlack"/> added
        /// below. Carrying the slack on the container (rather than inside each line's own markup)
        /// covers every line type at once, because the line host sizes to its items' margins.
        /// </summary>
        public Thickness LeftIndentMargin =>
            new Thickness(LeftIndent, VerticalPadding, 0, VerticalPadding + DescenderSlack);

        /// <summary>
        /// Whether this line renders at all. Bound on the item container so an empty line collapses
        /// completely (its container margin / line padding leaves no blank gap).
        /// </summary>
        public virtual Visibility LineVisibility => Visibility.Visible;
    }

    /// <summary>
    /// The header row: unlock header text, optional unlock datetime, and (toast only) the
    /// friend avatar.
    /// </summary>
    public sealed class ToastHeaderLine : ToastLineDescriptor
    {
        public ToastHeaderLine(
            AchievementToastViewModel parent,
            double fontSize,
            FontFamily fontFamily,
            Effect textShadow,
            bool showHeader,
            bool showUnlockTime,
            bool showDateSeparator,
            bool showFriendAvatar,
            string headerText,
            string completionHeaderText,
            string friendCompletionHeaderText)
            : base(parent, fontSize, fontFamily, textShadow)
        {
            ShowHeader = showHeader;
            ShowUnlockTime = showUnlockTime;
            ShowDateSeparator = showDateSeparator;
            ShowFriendAvatar = showFriendAvatar;
            HeaderText = headerText;
            CompletionHeaderText = completionHeaderText;
            FriendCompletionHeaderText = friendCompletionHeaderText;
        }

        public bool ShowHeader { get; }

        public bool ShowUnlockTime { get; }

        public bool ShowDateSeparator { get; }

        public bool ShowFriendAvatar { get; }

        /// <summary>
        /// The resolved unlock header text for this line's surface (the surface's user edit,
        /// falling back to the localized default; friend unlocks resolve their format here).
        /// </summary>
        public string HeaderText { get; }

        /// <summary>The resolved game-completion header text for this line's surface.</summary>
        public string CompletionHeaderText { get; }

        /// <summary>The resolved friend game-completion header text for this line's surface.</summary>
        public string FriendCompletionHeaderText { get; }

        // The header row still occupies space when it carries the unlock header text, the unlock
        // datetime, or the completion header; otherwise it collapses.
        public override Visibility LineVisibility =>
            (ShowHeader || ShowUnlockTime || Parent.IsGameCompleted)
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    /// <summary>
    /// The achievement title row.
    /// </summary>
    public sealed class ToastTitleLine : ToastLineDescriptor
    {
        public ToastTitleLine(
            AchievementToastViewModel parent,
            double fontSize,
            FontFamily fontFamily,
            Effect textShadow,
            bool showName,
            Brush titleBrush,
            Brush completedTitleBrush,
            bool showInlineBadge,
            object inlineBadgeSource,
            double inlineBadgeSize)
            : base(parent, fontSize, fontFamily, textShadow)
        {
            ShowName = showName;
            TitleBrush = titleBrush;
            CompletedTitleBrush = completedTitleBrush;
            ShowInlineBadge = showInlineBadge;
            InlineBadgeSource = inlineBadgeSource;
            InlineBadgeSize = inlineBadgeSize;
        }

        public bool ShowName { get; }

        public Brush TitleBrush { get; }

        /// <summary>
        /// The title color for the standalone "Game Complete!" notification, which honors this
        /// surface's rarity-colored-name toggle: the completed color when on, plain text when off.
        /// </summary>
        public Brush CompletedTitleBrush { get; }

        /// <summary>
        /// Whether the rarity/trophy badge is drawn inline, immediately before the name.
        /// </summary>
        public bool ShowInlineBadge { get; }

        /// <summary>
        /// The inline badge image: a path string for the toast (so animated GIFs play) or a
        /// pre-decoded ImageSource for the offscreen frame render.
        /// </summary>
        public object InlineBadgeSource { get; }

        /// <summary>
        /// Inline badge render size, resolved by the owning view model from the surface's
        /// badge-size setting (falling back to a title-relative ratio) so the badge is one
        /// consistent size across every rarity display mode.
        /// </summary>
        public double InlineBadgeSize { get; }

        // The title row shows for the achievement name, and always for the "Game Complete!" banner.
        public override Visibility LineVisibility =>
            (ShowName || Parent.IsGameCompleted) ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// The achievement description row.
    /// </summary>
    public sealed class ToastDescriptionLine : ToastLineDescriptor
    {
        public ToastDescriptionLine(
            AchievementToastViewModel parent,
            double fontSize,
            FontFamily fontFamily,
            Effect textShadow,
            bool showDescription,
            int maxLines)
            : base(parent, fontSize, fontFamily, textShadow)
        {
            ShowDescription = showDescription;
            MaxLines = maxLines < 1 ? 1 : maxLines;
        }

        public bool ShowDescription { get; }

        /// <summary>
        /// Wrapped-line budget for the description: two lines when no game/category line follows,
        /// one line when it does, so the card stays compact. Set by the owning view model.
        /// </summary>
        public int MaxLines { get; }

        /// <summary>
        /// One rendered line's height (DIPs), read from the font's own designed line spacing --
        /// which is the same metric WPF lays the text out with, so <see cref="MaxTextHeight"/>
        /// admits precisely <see cref="MaxLines"/> lines and lands on a line boundary.
        /// <para>
        /// This was a flat 1.4 em pinned onto the text with LineStackingStrategy="BlockLineHeight".
        /// That over-reserved 2-3 DIP per line for most faces (Source Sans Pro designs 1.256 em,
        /// Segoe UI 1.330), bought no descender safety -- the ink overhang is a sub-pixel residual
        /// either way, which <see cref="ToastLineDescriptor.DescenderSlack"/> covers -- and on a
        /// card with a fixed height was enough to push a wrapped description past the card and get
        /// its last line sliced. How far apart lines sit is the surface's line padding setting.
        /// </para>
        /// The template still turns off layout rounding on this text so the floating toast window
        /// cannot round the height below the exact lines and clip the last one (which the settings
        /// mockup, rendering without rounding, never does).
        /// </summary>
        public double LineBoxHeight => FontSize * (FontFamily?.LineSpacing ?? 0);

        /// <summary>
        /// The clamp height for <see cref="MaxLines"/> pinned line boxes. A sub-pixel epsilon guards
        /// against floating-point equality shaving the final line box. A description longer than its
        /// line budget is arranged at exactly this height and layout-clipped to it, so the bottom
        /// line's <see cref="ToastLineDescriptor.DescenderSlack"/> has to raise the ceiling too --
        /// the container margin that covers every other line sits outside this clip.
        /// </summary>
        public double MaxTextHeight => (LineBoxHeight * MaxLines) + DescenderSlack + 0.5;

        // Collapses when the description is hidden or the achievement has no description text.
        public override Visibility LineVisibility =>
            (ShowDescription && !string.IsNullOrWhiteSpace(Parent.Description))
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    /// <summary>
    /// The "game name • category" row.
    /// </summary>
    public sealed class ToastGameCategoryLine : ToastLineDescriptor
    {
        public ToastGameCategoryLine(
            AchievementToastViewModel parent,
            double fontSize,
            FontFamily fontFamily,
            Effect textShadow,
            bool showGameName,
            bool showCategory,
            bool showSeparator)
            : base(parent, fontSize, fontFamily, textShadow)
        {
            ShowGameName = showGameName;
            ShowCategory = showCategory;
            ShowSeparator = showSeparator;
        }

        public bool ShowGameName { get; }

        public bool ShowCategory { get; }

        public bool ShowSeparator { get; }

        // The game name, shown when the game-name setting is on. This applies to completion
        // notifications too (the setting governs whether the completed game's name appears).
        private string EffectiveGameName => ShowGameName ? Parent.GameName : null;

        private string EffectiveCategory => ShowCategory ? Parent.Category : null;

        /// <summary>
        /// The whole row composed into one string ("game • category") so a single bounded
        /// TextBlock can keep the category adjacent to the game name and trim the pair as a unit,
        /// instead of a horizontal panel that cannot trim its children or a dock layout that
        /// floats the category to the far edge.
        /// </summary>
        public string GameCategoryText
        {
            get
            {
                var name = EffectiveGameName;
                var category = EffectiveCategory;
                var hasName = !string.IsNullOrWhiteSpace(name);
                var hasCategory = !string.IsNullOrWhiteSpace(category);

                if (hasName && hasCategory)
                {
                    return ShowSeparator ? name + "  •  " + category : name + " " + category;
                }

                return hasName ? name : (hasCategory ? category : string.Empty);
            }
        }

        /// <summary>Collapses the row when neither the game name nor the category is shown.</summary>
        public bool HasGameCategoryContent => !string.IsNullOrEmpty(GameCategoryText);

        public override Visibility LineVisibility =>
            HasGameCategoryContent ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// The rarity percent text. Not a reorderable line: the surface templates draw the percent
    /// themselves (under the icon footer or under the right-side badge) and gate it on their own
    /// visibility flags, so this never joins the line list and takes no part in the line order or
    /// the bottom-line descender pass. It reuses the descriptor purely to carry the same resolved
    /// font values the real lines do, so the percent honors the same family and emphasis options.
    /// </summary>
    public sealed class ToastRarityTextLine : ToastLineDescriptor
    {
        public ToastRarityTextLine(
            AchievementToastViewModel parent,
            double fontSize,
            FontFamily fontFamily,
            Effect textShadow)
            : base(parent, fontSize, fontFamily, textShadow)
        {
        }
    }
}
