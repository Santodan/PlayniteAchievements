using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Threading;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Images;
using PlayniteAchievements.Services.UI;
using ObservableObject = PlayniteAchievements.Common.ObservableObject;

namespace PlayniteAchievements.ViewModels.Settings
{
    /// <summary>
    /// Backs one surface (toast or screenshot frame) of the notification appearance editor.
    /// Wraps the currently selected <see cref="NotificationStyleSettings"/> (the global
    /// default or a provider's whole-style copy) and observes its INPC objects: any edit
    /// schedules a debounced persist and raises <see cref="StyleChanged"/> so the host section
    /// can refresh its live mockups. Header texts are the exception: they commit only through
    /// <see cref="ApplyHeaderTexts"/> so format strings never persist per keystroke.
    /// </summary>
    internal sealed class NotificationAppearanceEditorViewModel : ObservableObject, IDisposable
    {
        private static IReadOnlyList<FontFamilyOption> _fontFamilyOptions;

        private readonly PlayniteAchievementsSettings _settings;
        private readonly PlayniteAchievementsPlugin _plugin;
        private readonly ILogger _logger;
        private readonly DispatcherTimer _persistDebounceTimer;
        private bool _hasPendingPersist;

        private NotificationStyleSettings _style;
        private NotificationSurfaceStyle _subscribedSurface;
        private NotificationBadgeImageSet _subscribedBadges;
        private NotificationHeaderTextSettings _subscribedHeaderTexts;
        private string _providerKey;
        private bool _isEditable = true;

        private string _unlockHeaderText;
        private string _friendUnlockHeaderText;
        private string _completionHeaderText;
        private string _friendCompletionHeaderText;
        private bool _hasHeaderFormatError;
        private string _cardWidthText = string.Empty;
        private string _cardMinHeightText = string.Empty;

        public NotificationAppearanceEditorViewModel(
            PlayniteAchievementsSettings settings,
            PlayniteAchievementsPlugin plugin,
            ILogger logger,
            bool isFrameSurface)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            _logger = logger;
            IsFrameSurface = isFrameSurface;

            _persistDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _persistDebounceTimer.Tick += OnPersistDebounceTimerTick;

            LineRows = new ObservableCollection<NotificationLineRowItem>(
                NotificationSurfaceStyle.DefaultLineOrder.Select(kind =>
                    new NotificationLineRowItem(kind, BuildLineDisplayName(kind), OnLineRowSizeEdited)));
        }

        /// <summary>
        /// Raised after any observed style edit (and after header Apply) so the host section
        /// can rebuild its mockups from the edited values.
        /// </summary>
        public event EventHandler StyleChanged;

        public bool IsFrameSurface { get; }

        public bool IsToastSurface => !IsFrameSurface;

        /// <summary>
        /// The style object being edited: the global default or a provider's copy. Null-safe
        /// for bindings while no style is set.
        /// </summary>
        public NotificationStyleSettings Style => _style;

        public NotificationSurfaceStyle Surface =>
            IsFrameSurface ? _style?.Frame : _style?.Toast;

        /// <summary>
        /// The provider key owning the edited copy, or null when editing the global default.
        /// Also selects the notification image slot folder.
        /// </summary>
        public string ProviderKey => _providerKey;

        /// <summary>
        /// False when a platform without a custom style is selected: the editor then shows the
        /// default values read-only until the user customizes the platform.
        /// </summary>
        public bool IsEditable => _isEditable;

        public ObservableCollection<NotificationLineRowItem> LineRows { get; }

        public IReadOnlyList<FontFamilyOption> FontFamilyOptions =>
            _fontFamilyOptions ?? (_fontFamilyOptions = BuildFontFamilyOptions());

        public FontFamilyOption SelectedFontFamilyOption
        {
            get
            {
                var familyName = Surface?.FontFamily;
                if (string.IsNullOrWhiteSpace(familyName))
                {
                    return FontFamilyOptions.FirstOrDefault();
                }

                return FontFamilyOptions.FirstOrDefault(option =>
                           string.Equals(option.FamilyName, familyName, StringComparison.OrdinalIgnoreCase))
                       ?? FontFamilyOptions.FirstOrDefault();
            }
            set
            {
                var surface = Surface;
                if (surface == null)
                {
                    return;
                }

                var familyName = value?.FamilyName;
                if (!string.Equals(surface.FontFamily, familyName, StringComparison.Ordinal))
                {
                    surface.FontFamily = familyName;
                }
            }
        }

        #region Card dimensions (toast surface only; blank = template default)

        /// <summary>
        /// Toast card width text (LostFocus commit). Blank or invalid clears the override
        /// (falls back to the default width); a positive number stores it.
        /// </summary>
        public string CardWidthText
        {
            get => _cardWidthText;
            set
            {
                if (SetValueAndReturn(ref _cardWidthText, value))
                {
                    CommitCardDimension(value, isWidth: true);
                }
            }
        }

        /// <summary>
        /// Toast card minimum-height text (LostFocus commit). Blank or invalid clears the
        /// override; a positive number stores it. The card still grows for taller content.
        /// </summary>
        public string CardMinHeightText
        {
            get => _cardMinHeightText;
            set
            {
                if (SetValueAndReturn(ref _cardMinHeightText, value))
                {
                    CommitCardDimension(value, isWidth: false);
                }
            }
        }

        private void CommitCardDimension(string text, bool isWidth)
        {
            var surface = Surface;
            if (surface == null || !_isEditable)
            {
                RefreshCardDimensions();
                return;
            }

            double? parsed = null;
            if (!string.IsNullOrWhiteSpace(text))
            {
                if (double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var current) &&
                    current > 0)
                {
                    parsed = current;
                }
                else if (double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var invariant) &&
                         invariant > 0)
                {
                    parsed = invariant;
                }
            }

            if (isWidth)
            {
                surface.CardWidth = parsed;
            }
            else
            {
                surface.CardMinHeight = parsed;
            }

            RefreshCardDimensions();
        }

        private void RefreshCardDimensions()
        {
            var surface = Surface;
            SetValue(ref _cardWidthText,
                surface?.CardWidth?.ToString(CultureInfo.CurrentCulture) ?? string.Empty,
                nameof(CardWidthText));
            SetValue(ref _cardMinHeightText,
                surface?.CardMinHeight?.ToString(CultureInfo.CurrentCulture) ?? string.Empty,
                nameof(CardMinHeightText));
        }

        #endregion

        #region Header texts (pending until Apply; toast surface hosts the shared group)

        public string UnlockHeaderText
        {
            get => _unlockHeaderText;
            set => SetValue(ref _unlockHeaderText, value);
        }

        public string FriendUnlockHeaderText
        {
            get => _friendUnlockHeaderText;
            set => SetValue(ref _friendUnlockHeaderText, value);
        }

        public string CompletionHeaderText
        {
            get => _completionHeaderText;
            set => SetValue(ref _completionHeaderText, value);
        }

        public string FriendCompletionHeaderText
        {
            get => _friendCompletionHeaderText;
            set => SetValue(ref _friendCompletionHeaderText, value);
        }

        public bool HasHeaderFormatError
        {
            get => _hasHeaderFormatError;
            private set => SetValue(ref _hasHeaderFormatError, value);
        }

        /// <summary>
        /// Commits the pending header texts to the style. Blank or default-equal values store
        /// null (keep following the localized default). A non-blank friend format missing its
        /// {0} placeholder is kept pending and flagged instead of being stored.
        /// </summary>
        public void ApplyHeaderTexts()
        {
            var texts = _style?.HeaderTexts;
            if (texts == null || !_isEditable)
            {
                return;
            }

            texts.UnlockHeader = NotificationHeaderTextService.NormalizeForStore(
                UnlockHeaderText, NotificationHeaderTextService.GetDefaultUnlockHeader());
            texts.CompletionHeader = NotificationHeaderTextService.NormalizeForStore(
                CompletionHeaderText, NotificationHeaderTextService.GetDefaultCompletionHeader());

            var hasError = false;
            if (string.IsNullOrWhiteSpace(FriendUnlockHeaderText) ||
                NotificationHeaderTextService.IsValidHeaderFormat(FriendUnlockHeaderText))
            {
                texts.FriendUnlockHeaderFormat = NotificationHeaderTextService.NormalizeForStore(
                    FriendUnlockHeaderText, NotificationHeaderTextService.GetDefaultFriendUnlockHeaderFormat());
            }
            else
            {
                hasError = true;
            }

            if (string.IsNullOrWhiteSpace(FriendCompletionHeaderText) ||
                NotificationHeaderTextService.IsValidHeaderFormat(FriendCompletionHeaderText))
            {
                texts.FriendCompletionHeaderFormat = NotificationHeaderTextService.NormalizeForStore(
                    FriendCompletionHeaderText, NotificationHeaderTextService.GetDefaultFriendCompletionHeaderFormat());
            }
            else
            {
                hasError = true;
            }

            HasHeaderFormatError = hasError;
            RefreshHeaderTexts(keepInvalidPending: true);
        }

        private void RefreshHeaderTexts(bool keepInvalidPending = false)
        {
            var texts = _style?.HeaderTexts;
            UnlockHeaderText = texts?.UnlockHeader
                ?? NotificationHeaderTextService.GetDefaultUnlockHeader();
            CompletionHeaderText = texts?.CompletionHeader
                ?? NotificationHeaderTextService.GetDefaultCompletionHeader();

            if (!keepInvalidPending || !HasHeaderFormatError)
            {
                FriendUnlockHeaderText = texts?.FriendUnlockHeaderFormat
                    ?? NotificationHeaderTextService.GetDefaultFriendUnlockHeaderFormat();
                FriendCompletionHeaderText = texts?.FriendCompletionHeaderFormat
                    ?? NotificationHeaderTextService.GetDefaultFriendCompletionHeaderFormat();
            }
        }

        #endregion

        #region Images (toast surface hosts the shared background and badge groups)

        /// <summary>
        /// Copies the picked file or URL into managed storage for the slot and stores the
        /// resulting path on the style. No-ops when materialization fails.
        /// </summary>
        public async Task ApplyImageAsync(NotificationImageSlot slot, string sourcePathOrUrl)
        {
            if (_style == null || !_isEditable)
            {
                return;
            }

            try
            {
                var resolved = await _plugin.NotificationImageStore.MaterializeAsync(
                    sourcePathOrUrl, _providerKey, slot, CancellationToken.None);
                if (resolved != null)
                {
                    SetImagePath(slot, resolved);
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"Failed to apply notification image for slot {slot}.");
            }
        }

        /// <summary>
        /// Deletes the slot's managed image files and clears the stored path.
        /// </summary>
        public void ClearImage(NotificationImageSlot slot)
        {
            if (_style == null || !_isEditable)
            {
                return;
            }

            _plugin.NotificationImageStore.DeleteSlot(_providerKey, slot);
            SetImagePath(slot, null);
        }

        private void SetImagePath(NotificationImageSlot slot, string path)
        {
            switch (slot)
            {
                case NotificationImageSlot.Background:
                    _style.ToastBackgroundImagePath = path;
                    break;
                case NotificationImageSlot.BadgeCommon:
                    _style.BadgeImages.CommonPath = path;
                    break;
                case NotificationImageSlot.BadgeUncommon:
                    _style.BadgeImages.UncommonPath = path;
                    break;
                case NotificationImageSlot.BadgeRare:
                    _style.BadgeImages.RarePath = path;
                    break;
                case NotificationImageSlot.BadgeUltraRare:
                    _style.BadgeImages.UltraRarePath = path;
                    break;
                case NotificationImageSlot.BadgeCompletion:
                    _style.BadgeImages.CompletionPath = path;
                    break;
            }
        }

        #endregion

        #region Line order and sizes

        /// <summary>
        /// Moves the dragged line kinds before or after the target kind and stores the new
        /// order. Returns true when the order changed.
        /// </summary>
        public bool MoveLines(List<string> draggedKinds, string targetKind, bool insertAfter)
        {
            return ApplyLineOrder(order =>
            {
                var dragged = order
                    .Where(kind => draggedKinds.Contains(kind, StringComparer.OrdinalIgnoreCase))
                    .ToList();
                if (dragged.Count == 0 ||
                    dragged.Contains(targetKind, StringComparer.OrdinalIgnoreCase))
                {
                    return null;
                }

                var remaining = order.Except(dragged, StringComparer.OrdinalIgnoreCase).ToList();
                var targetIndex = remaining.FindIndex(kind =>
                    string.Equals(kind, targetKind, StringComparison.OrdinalIgnoreCase));
                if (targetIndex < 0)
                {
                    return null;
                }

                remaining.InsertRange(insertAfter ? targetIndex + 1 : targetIndex, dragged);
                return remaining;
            });
        }

        /// <summary>
        /// Moves the dragged line kinds to the end of the order. Returns true when changed.
        /// </summary>
        public bool MoveLinesToEnd(List<string> draggedKinds)
        {
            return ApplyLineOrder(order =>
            {
                var dragged = order
                    .Where(kind => draggedKinds.Contains(kind, StringComparer.OrdinalIgnoreCase))
                    .ToList();
                if (dragged.Count == 0)
                {
                    return null;
                }

                var remaining = order.Except(dragged, StringComparer.OrdinalIgnoreCase).ToList();
                remaining.AddRange(dragged);
                return remaining;
            });
        }

        private bool ApplyLineOrder(Func<List<string>, List<string>> reorder)
        {
            var surface = Surface;
            if (surface == null || !_isEditable)
            {
                return false;
            }

            var order = NotificationSurfaceStyle.CanonicalizeLineOrder(surface.LineOrder);
            var next = reorder(order);
            if (next == null || next.SequenceEqual(order, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }

            surface.LineOrder = new List<string>(next);
            return true;
        }

        private void OnLineRowSizeEdited(NotificationLineRowItem row, string text)
        {
            var surface = Surface;
            if (surface == null || !_isEditable)
            {
                SyncLineRows();
                return;
            }

            double? size = null;
            if (!string.IsNullOrWhiteSpace(text))
            {
                if (double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var current) &&
                    current > 0)
                {
                    size = current;
                }
                else if (double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var invariant) &&
                         invariant > 0)
                {
                    size = invariant;
                }
            }

            SetLineSize(surface, row.Kind, size);
            RefreshLineSizes();
        }

        private static void SetLineSize(NotificationSurfaceStyle surface, string kind, double? size)
        {
            switch (kind)
            {
                case NotificationSurfaceStyle.LineHeader:
                case NotificationSurfaceStyle.LineGameCategory:
                    // The header/caption size drives both the header and game/category lines.
                    surface.HeaderFontSize = size;
                    break;
                case NotificationSurfaceStyle.LineTitle:
                    surface.TitleFontSize = size;
                    break;
                case NotificationSurfaceStyle.LineDescription:
                    surface.BodyFontSize = size;
                    break;
            }
        }

        private static double? GetLineSize(NotificationSurfaceStyle surface, string kind)
        {
            switch (kind)
            {
                case NotificationSurfaceStyle.LineHeader:
                case NotificationSurfaceStyle.LineGameCategory:
                    return surface?.HeaderFontSize;
                case NotificationSurfaceStyle.LineTitle:
                    return surface?.TitleFontSize;
                case NotificationSurfaceStyle.LineDescription:
                    return surface?.BodyFontSize;
                default:
                    return null;
            }
        }

        /// <summary>
        /// Reorders the stable row instances to match the stored line order (preserving grid
        /// selection) and refreshes their size texts.
        /// </summary>
        private void SyncLineRows()
        {
            var order = NotificationSurfaceStyle.CanonicalizeLineOrder(Surface?.LineOrder);
            for (var target = 0; target < order.Count && target < LineRows.Count; target++)
            {
                var current = -1;
                for (var i = target; i < LineRows.Count; i++)
                {
                    if (string.Equals(LineRows[i].Kind, order[target], StringComparison.OrdinalIgnoreCase))
                    {
                        current = i;
                        break;
                    }
                }

                if (current > target)
                {
                    LineRows.Move(current, target);
                }
            }

            RefreshLineSizes();
        }

        private void RefreshLineSizes()
        {
            var surface = Surface;
            foreach (var row in LineRows)
            {
                var size = GetLineSize(surface, row.Kind);
                row.UpdateSizeText(size?.ToString(CultureInfo.CurrentCulture) ?? string.Empty);
            }
        }

        private static string BuildLineDisplayName(string kind)
        {
            switch (kind)
            {
                case NotificationSurfaceStyle.LineHeader:
                    return L("LOCPlayAch_Settings_ToastShowHeader");
                case NotificationSurfaceStyle.LineTitle:
                    return L("LOCPlayAch_Settings_ToastShowName");
                case NotificationSurfaceStyle.LineDescription:
                    return L("LOCPlayAch_Settings_ToastShowDescription");
                case NotificationSurfaceStyle.LineGameCategory:
                    return L("LOCPlayAch_Settings_ToastShowGameName") + " / " + L("LOCPlayAch_Common_Label_Category");
                default:
                    return kind;
            }
        }

        #endregion

        /// <summary>
        /// Points the editor at a new style object (global default or a provider copy).
        /// Pending edits against the previous style are flushed first.
        /// </summary>
        public void SetStyle(NotificationStyleSettings style, string providerKey, bool isEditable)
        {
            FlushPendingPersist();
            Unsubscribe();

            _style = style;
            _providerKey = string.IsNullOrWhiteSpace(providerKey) ? null : providerKey;
            _isEditable = isEditable;

            Subscribe();
            SyncLineRows();
            RefreshCardDimensions();
            HasHeaderFormatError = false;
            RefreshHeaderTexts();

            OnPropertyChanged(nameof(Style));
            OnPropertyChanged(nameof(Surface));
            OnPropertyChanged(nameof(ProviderKey));
            OnPropertyChanged(nameof(IsEditable));
            OnPropertyChanged(nameof(SelectedFontFamilyOption));
        }

        private void Subscribe()
        {
            if (_style == null)
            {
                return;
            }

            _style.PropertyChanged += OnStyleObjectPropertyChanged;
            _subscribedSurface = Surface;
            if (_subscribedSurface != null)
            {
                _subscribedSurface.PropertyChanged += OnSurfacePropertyChanged;
            }

            // The shared background/badge/header groups are hosted by the toast editor only,
            // so only it observes (and persists) those objects.
            if (IsToastSurface)
            {
                _subscribedBadges = _style.BadgeImages;
                _subscribedBadges.PropertyChanged += OnStyleObjectPropertyChanged;
                _subscribedHeaderTexts = _style.HeaderTexts;
                _subscribedHeaderTexts.PropertyChanged += OnStyleObjectPropertyChanged;
            }
        }

        private void Unsubscribe()
        {
            if (_style != null)
            {
                _style.PropertyChanged -= OnStyleObjectPropertyChanged;
            }

            if (_subscribedSurface != null)
            {
                _subscribedSurface.PropertyChanged -= OnSurfacePropertyChanged;
                _subscribedSurface = null;
            }

            if (_subscribedBadges != null)
            {
                _subscribedBadges.PropertyChanged -= OnStyleObjectPropertyChanged;
                _subscribedBadges = null;
            }

            if (_subscribedHeaderTexts != null)
            {
                _subscribedHeaderTexts.PropertyChanged -= OnStyleObjectPropertyChanged;
                _subscribedHeaderTexts = null;
            }
        }

        private void OnStyleObjectPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            // Frame-only style edits route through the frame editor's surface handler; the
            // toast editor owns the shared style-level objects.
            if (ReferenceEquals(sender, _style) &&
                (e.PropertyName == nameof(NotificationStyleSettings.Toast) ||
                 e.PropertyName == nameof(NotificationStyleSettings.Frame) ||
                 e.PropertyName == nameof(NotificationStyleSettings.BadgeImages) ||
                 e.PropertyName == nameof(NotificationStyleSettings.HeaderTexts)))
            {
                // A child object was replaced wholesale; resubscribe to the new instances.
                Unsubscribe();
                Subscribe();
            }

            if (ReferenceEquals(sender, _style) &&
                e.PropertyName == nameof(NotificationStyleSettings.ToastBackgroundImagePath))
            {
                OnPropertyChanged(nameof(Style));
            }

            NotifyStyleEdited();
        }

        private void OnSurfacePropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(NotificationSurfaceStyle.LineOrder))
            {
                SyncLineRows();
            }
            else if (e.PropertyName == nameof(NotificationSurfaceStyle.HeaderFontSize) ||
                     e.PropertyName == nameof(NotificationSurfaceStyle.TitleFontSize) ||
                     e.PropertyName == nameof(NotificationSurfaceStyle.BodyFontSize))
            {
                RefreshLineSizes();
            }
            else if (e.PropertyName == nameof(NotificationSurfaceStyle.FontFamily))
            {
                OnPropertyChanged(nameof(SelectedFontFamilyOption));
            }
            else if (e.PropertyName == nameof(NotificationSurfaceStyle.CardWidth) ||
                     e.PropertyName == nameof(NotificationSurfaceStyle.CardMinHeight))
            {
                RefreshCardDimensions();
            }

            NotifyStyleEdited();
        }

        private void NotifyStyleEdited()
        {
            if (!_isEditable)
            {
                return;
            }

            SchedulePersist();
            StyleChanged?.Invoke(this, EventArgs.Empty);
        }

        private void SchedulePersist()
        {
            _hasPendingPersist = true;
            _persistDebounceTimer.Stop();
            _persistDebounceTimer.Start();
        }

        private void OnPersistDebounceTimerTick(object sender, EventArgs e)
        {
            FlushPendingPersist();
        }

        /// <summary>
        /// Persists a pending debounced change immediately. Called from the debounce timer
        /// tick, before style swaps, and from Dispose so closing settings never drops an edit.
        /// </summary>
        public void FlushPendingPersist()
        {
            _persistDebounceTimer.Stop();
            if (!_hasPendingPersist)
            {
                return;
            }

            _hasPendingPersist = false;
            try
            {
                if (_providerKey != null && _style != null)
                {
                    // Re-store the edited copy; the setter clones it and raises
                    // PropertyChanged(ProviderNotificationStyles).
                    _settings.Persisted?.SetProviderNotificationStyle(_providerKey, _style);
                }

                _plugin.PersistSettingsForUi();
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "Failed to persist notification appearance settings.");
            }
        }

        public void Dispose()
        {
            FlushPendingPersist();
            _persistDebounceTimer.Tick -= OnPersistDebounceTimerTick;
            Unsubscribe();
        }

        private static IReadOnlyList<FontFamilyOption> BuildFontFamilyOptions()
        {
            var options = new List<FontFamilyOption>
            {
                new FontFamilyOption(
                    L("LOCPlayAch_Settings_Style_FontThemeDefault"),
                    familyName: null,
                    previewFamily: System.Windows.SystemFonts.MessageFontFamily)
            };

            options.AddRange(Fonts.SystemFontFamilies
                .Select(family => new FontFamilyOption(family.Source, family.Source, family))
                .OrderBy(option => option.DisplayName, StringComparer.CurrentCultureIgnoreCase));

            return options;
        }

        private static string L(string key)
        {
            return ResourceProvider.GetString(key);
        }
    }

    /// <summary>
    /// One entry of the font family picker. A null <see cref="FamilyName"/> means the theme
    /// default; <see cref="PreviewFamily"/> is never null so item rendering has a valid font.
    /// </summary>
    internal sealed class FontFamilyOption
    {
        public FontFamilyOption(string displayName, string familyName, FontFamily previewFamily)
        {
            DisplayName = displayName;
            FamilyName = familyName;
            PreviewFamily = previewFamily;
        }

        public string DisplayName { get; }

        public string FamilyName { get; }

        public FontFamily PreviewFamily { get; }
    }

    /// <summary>
    /// One draggable text line row of the appearance editor: the line kind token, its
    /// localized name, and its editable font size text (blank = theme default).
    /// </summary>
    internal sealed class NotificationLineRowItem : ObservableObject
    {
        private readonly Action<NotificationLineRowItem, string> _onSizeEdited;
        private string _sizeText = string.Empty;

        public NotificationLineRowItem(
            string kind,
            string displayName,
            Action<NotificationLineRowItem, string> onSizeEdited)
        {
            Kind = kind;
            DisplayName = displayName;
            _onSizeEdited = onSizeEdited;
        }

        public string Kind { get; }

        public string DisplayName { get; }

        /// <summary>
        /// Editable size text (LostFocus commit). Setting it routes through the owner, which
        /// parses and stores the size; the displayed text is then refreshed via
        /// <see cref="UpdateSizeText"/> so invalid input snaps back to the stored value.
        /// </summary>
        public string SizeText
        {
            get => _sizeText;
            set
            {
                if (!SetValueAndReturn(ref _sizeText, value))
                {
                    return;
                }

                _onSizeEdited?.Invoke(this, value);
            }
        }

        /// <summary>
        /// Updates the displayed size text without routing back through the edit callback.
        /// </summary>
        public void UpdateSizeText(string value)
        {
            SetValue(ref _sizeText, value, nameof(SizeText));
        }
    }
}
