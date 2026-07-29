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
            bool showFriendAvatar)
            : base(parent, fontSize, fontFamily)
        {
            ShowHeader = showHeader;
            ShowUnlockTime = showUnlockTime;
            ShowDateSeparator = showDateSeparator;
            ShowFriendAvatar = showFriendAvatar;
        }

        public bool ShowHeader { get; }

        public bool ShowUnlockTime { get; }

        public bool ShowDateSeparator { get; }

        public bool ShowFriendAvatar { get; }
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
            bool showInlineBadge,
            object inlineBadgeSource)
            : base(parent, fontSize, fontFamily)
        {
            ShowName = showName;
            TitleBrush = titleBrush;
            ShowInlineBadge = showInlineBadge;
            InlineBadgeSource = inlineBadgeSource;
        }

        public bool ShowName { get; }

        public Brush TitleBrush { get; }

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
        /// Inline badge render size, slightly larger than the title text. Uses the same
        /// title-relative ratio as the footer badge so the badge is one consistent size across
        /// every rarity display mode.
        /// </summary>
        public double InlineBadgeSize => FontSize * AchievementToastViewModel.BadgeToTitleRatio;

        /// <summary>
        /// Top margin for the title row. The inline badge's left edge stays flush with the
        /// text lines below (both start at x=0); the badge sits before the name rather than
        /// being pulled left to center on the first-letter column.
        /// </summary>
        public Thickness InlineBadgeLineMargin => new Thickness(0, 1, 0, 0);
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
        /// The clamp height for <see cref="MaxLines"/> wrapped lines at the row's font size,
        /// scaling with larger fonts (replaces the old fixed pixel clamp).
        /// </summary>
        public double MaxTextHeight => FontSize * 1.4 * MaxLines;
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

        // The game name (also the completion message's subject) when shown.
        private string EffectiveGameName =>
            (ShowGameName || Parent.IsGameCompleted) ? Parent.GameName : null;

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
    }
}
