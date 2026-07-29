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
            bool showDescription)
            : base(parent, fontSize, fontFamily)
        {
            ShowDescription = showDescription;
        }

        public bool ShowDescription { get; }

        /// <summary>
        /// Two wrapped lines at the row's font size, replacing the old fixed pixel clamp so
        /// larger fonts still show two lines.
        /// </summary>
        public double MaxTextHeight => FontSize * 1.4 * 2;
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
    }
}
