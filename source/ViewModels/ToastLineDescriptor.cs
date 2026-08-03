using System.Windows;
using System.Windows.Media;

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
        protected ToastLineDescriptor(AchievementToastViewModel parent, double fontSize, FontFamily fontFamily)
        {
            Parent = parent;
            FontSize = fontSize;
            FontFamily = fontFamily;
        }

        public AchievementToastViewModel Parent { get; }

        public double FontSize { get; }

        // Bound as a local value on each line TextBlock: the shared TextBlock base style sets
        // FontFamily via a style setter, which would beat an inherited TextElement.FontFamily.
        public FontFamily FontFamily { get; }

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
        /// The line's container margin: the <see cref="LeftIndent"/> on the left and the
        /// <see cref="VerticalPadding"/> on top and bottom.
        /// </summary>
        public Thickness LeftIndentMargin => new Thickness(LeftIndent, VerticalPadding, 0, VerticalPadding);

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
            bool showHeader,
            bool showUnlockTime,
            bool showDateSeparator,
            bool showFriendAvatar,
            string headerText,
            string completionHeaderText,
            string friendCompletionHeaderText)
            : base(parent, fontSize, fontFamily)
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
            bool showName,
            Brush titleBrush,
            Brush completedTitleBrush,
            bool showInlineBadge,
            object inlineBadgeSource,
            double inlineBadgeSize)
            : base(parent, fontSize, fontFamily)
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
            bool showDescription,
            int maxLines)
            : base(parent, fontSize, fontFamily)
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
        /// Fixed line-box height (DIPs) for the description. Paired with
        /// LineStackingStrategy="BlockLineHeight" in the template, every wrapped line occupies
        /// exactly this height, so <see cref="MaxTextHeight"/> admits precisely
        /// <see cref="MaxLines"/> lines. The template also turns off layout rounding on this text so
        /// the floating toast window cannot round the height below the exact line boxes and clip the
        /// last line (which the settings mockup, rendering without rounding, never does).
        /// </summary>
        public double LineBoxHeight => FontSize * 1.4;

        /// <summary>
        /// The clamp height for <see cref="MaxLines"/> pinned line boxes. A sub-pixel epsilon guards
        /// against floating-point equality shaving the final line box.
        /// </summary>
        public double MaxTextHeight => (LineBoxHeight * MaxLines) + 0.5;

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
            bool showGameName,
            bool showCategory,
            bool showSeparator)
            : base(parent, fontSize, fontFamily)
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

        /// <summary>
        /// Fixed line-box height (DIPs), matching the description line's rhythm
        /// (<see cref="ToastDescriptionLine.LineBoxHeight"/>), so the game/category row reserves the
        /// same vertical leading as the other text lines instead of rendering cramped against the
        /// line above it.
        /// </summary>
        public double LineBoxHeight => FontSize * 1.4;

        /// <summary>Collapses the row when neither the game name nor the category is shown.</summary>
        public bool HasGameCategoryContent => !string.IsNullOrEmpty(GameCategoryText);

        public override Visibility LineVisibility =>
            HasGameCategoryContent ? Visibility.Visible : Visibility.Collapsed;
    }
}
