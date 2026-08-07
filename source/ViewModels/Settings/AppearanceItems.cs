using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using Playnite.SDK;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Services.UI;

namespace PlayniteAchievements.ViewModels.Settings
{
    public sealed class ProviderAppearanceItem : PlayniteAchievements.Common.ObservableObject
    {
        private readonly IDataProvider _provider;
        private readonly PersistedSettings _settings;
        private readonly Action _applyAppearance;

        public ProviderAppearanceItem(
            IDataProvider provider,
            PersistedSettings settings,
            Action applyAppearance)
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _applyAppearance = applyAppearance;
        }

        public string ProviderKey => _provider.ProviderKey;

        public string DisplayName => ProviderRegistry.GetLocalizedName(ProviderKey);

        public string ProviderIconKey => _provider.ProviderIconKey;

        public string DefaultColor => _provider.ProviderColorHex;

        public string BaseColor
        {
            get
            {
                var overrides = _settings.ProviderColorOverrides;
                return overrides != null &&
                       overrides.TryGetValue(ProviderKey, out var color)
                    ? color
                    : DefaultColor;
            }
            set
            {
                var overrides = _settings.ProviderColorOverrides != null
                    ? new Dictionary<string, string>(
                        _settings.ProviderColorOverrides,
                        StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var normalized = value?.Trim();

                if (string.IsNullOrWhiteSpace(normalized) ||
                    string.Equals(normalized, DefaultColor, StringComparison.OrdinalIgnoreCase))
                {
                    overrides.Remove(ProviderKey);
                }
                else
                {
                    overrides[ProviderKey] = normalized;
                }

                _settings.ProviderColorOverrides = overrides;
                _applyAppearance?.Invoke();
                Refresh();
            }
        }

        public string EffectiveColor => ProviderRegistry.GetProviderColorHex(ProviderKey, DefaultColor);

        public Brush PreviewBrush => CreateBrush(EffectiveColor);

        public void Reset()
        {
            var overrides = _settings.ProviderColorOverrides != null
                ? new Dictionary<string, string>(
                    _settings.ProviderColorOverrides,
                    StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            overrides.Remove(ProviderKey);
            _settings.ProviderColorOverrides = overrides;
            _applyAppearance?.Invoke();
            Refresh();
        }

        public void Refresh()
        {
            OnPropertyChanged(nameof(BaseColor));
            OnPropertyChanged(nameof(EffectiveColor));
            OnPropertyChanged(nameof(PreviewBrush));
        }

        private static Brush CreateBrush(string colorText)
        {
            try
            {
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorText));
                if (brush.CanFreeze)
                {
                    brush.Freeze();
                }

                return brush;
            }
            catch
            {
                return Brushes.Transparent;
            }
        }
    }

    public sealed class ResourceAppearanceItem : PlayniteAchievements.Common.ObservableObject
    {
        private readonly PersistedSettings _settings;
        private readonly Action _applyResources;
        private ResourceOverrideMode _mode;
        private string _customValue;

        public ResourceAppearanceItem(
            ResourceOverrideDescriptor descriptor,
            PersistedSettings settings,
            Action applyResources)
        {
            Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _applyResources = applyResources;

            if (_settings.ResourceOverrides != null &&
                _settings.ResourceOverrides.TryGetValue(descriptor.ResourceKey, out var persisted) &&
                persisted != null)
            {
                _mode = persisted.Mode;
                _customValue = persisted.CustomValue;
            }
            else
            {
                _mode = ResourceOverrideMode.FollowPlaynite;
                _customValue = GetCurrentPlayniteValueText(descriptor);
            }
        }

        public ResourceOverrideDescriptor Descriptor { get; }
        public string DisplayName => ResourceProvider.GetString(Descriptor.DisplayName);
        public string ResourceKey => Descriptor.ResourceKey;
        public string PlayniteResourceKey => Descriptor.PlayniteResourceKey;
        public bool IsBrush => Descriptor.ValueKind == ResourceOverrideValueKind.Brush;
        public bool IsFontSize => Descriptor.ValueKind == ResourceOverrideValueKind.FontSize;
        public bool IsFontFamily => Descriptor.ValueKind == ResourceOverrideValueKind.FontFamily;

        private static readonly IReadOnlyList<string> _fontSizeOptions = new[]
        {
            "10", "11", "12", "13", "14", "15", "16", "18", "20", "22", "24", "28", "32"
        };

        /// <summary>
        /// Sizes offered by the font-size dropdown. The dropdown stays editable, so a size that is
        /// not listed can still be typed. An instance property because WPF bindings do not resolve
        /// static members by name.
        /// </summary>
        public IReadOnlyList<string> FontSizeOptions => _fontSizeOptions;

        /// <summary>
        /// This row's own copy of the font list, so type-to-filter here cannot disturb another
        /// dropdown's selection (see <see cref="FontFamilyOption"/> use in the notification editor).
        /// Only built for the font-family row; every other row leaves it null.
        /// </summary>
        public IReadOnlyList<FontFamilyOption> FontFamilyOptions =>
            !IsFontFamily
                ? null
                : _fontFamilyOptions ?? (_fontFamilyOptions = BuildFontFamilyOptions());

        private IReadOnlyList<FontFamilyOption> _fontFamilyOptions;

        private IReadOnlyList<FontFamilyOption> BuildFontFamilyOptions()
        {
            var options = new List<FontFamilyOption>(SystemFontCatalog.Families);

            // Keep whatever is stored selectable even when the catalog has no such font, because a
            // Selector reports a value missing from its items back as null - which would overwrite
            // the override with nothing.
            var current = DisplayValueText;
            if (!string.IsNullOrWhiteSpace(current)
                && !options.Exists(option =>
                    string.Equals(option.FamilyName, current, StringComparison.OrdinalIgnoreCase)))
            {
                options.Insert(0, SystemFontCatalog.CreateUnlistedOption(current));
            }

            return options;
        }

        public ResourceOverrideMode Mode
        {
            get => _mode;
            set
            {
                if (SetValueAndReturn(ref _mode, value))
                {
                    if (_mode == ResourceOverrideMode.Custom && string.IsNullOrWhiteSpace(_customValue))
                    {
                        _customValue = GetCurrentPlayniteValueText(Descriptor);
                        OnPropertyChanged(nameof(CustomValue));
                    }

                    Persist();
                    OnPropertyChanged(nameof(IsCustom));
                    OnPropertyChanged(nameof(IsTransparent));
                    OnPropertyChanged(nameof(DisplayValueText));
                    OnPropertyChanged(nameof(PreviewBrush));

                    // The switch to Custom changes DisplayValueText, so the font list has to be
                    // rebuilt in case the newly shown family is one the catalog does not list.
                    _fontFamilyOptions = null;
                    OnPropertyChanged(nameof(FontFamilyOptions));
                }
            }
        }

        public bool IsCustom => Mode == ResourceOverrideMode.Custom;

        public string CustomValue
        {
            get => _customValue;
            set
            {
                if (SetValueAndReturn(ref _customValue, value))
                {
                    Persist();
                    OnPropertyChanged(nameof(DisplayValueText));
                    OnPropertyChanged(nameof(PreviewBrush));
                }
            }
        }

        public bool IsTransparent => Mode == ResourceOverrideMode.Transparent;

        public string DisplayValueText
        {
            get => IsTransparent
                ? PlayAchResourceService.TransparentValue
                : IsCustom ? CustomValue : GetCurrentPlayniteValueText(Descriptor);
            set
            {
                if (!IsCustom)
                {
                    return;
                }

                CustomValue = value;
            }
        }

        public Brush PreviewBrush
        {
            get
            {
                try
                {
                    return new SolidColorBrush((Color)ColorConverter.ConvertFromString(DisplayValueText));
                }
                catch
                {
                    return Brushes.Transparent;
                }
            }
        }

        private void Persist()
        {
            if (_settings.ResourceOverrides == null)
            {
                _settings.ResourceOverrides = new Dictionary<string, ResourceOverrideSetting>(StringComparer.OrdinalIgnoreCase);
            }

            if (Mode == ResourceOverrideMode.FollowPlaynite)
            {
                _settings.ResourceOverrides.Remove(ResourceKey);
                _settings.OnPropertyChanged(nameof(PersistedSettings.ResourceOverrides));
                _applyResources?.Invoke();
                return;
            }

            _settings.ResourceOverrides[ResourceKey] = new ResourceOverrideSetting
            {
                Mode = Mode,
                CustomValue = Mode == ResourceOverrideMode.Transparent
                    ? PlayAchResourceService.TransparentValue
                    : CustomValue
            };

            _settings.OnPropertyChanged(nameof(PersistedSettings.ResourceOverrides));
            _applyResources?.Invoke();
        }

        private static string GetCurrentPlayniteValueText(ResourceOverrideDescriptor descriptor)
        {
            var value = FindPlayniteResourceValue(descriptor);
            switch (descriptor.ValueKind)
            {
                case ResourceOverrideValueKind.Brush:
                    return BrushToText(value as Brush);

                case ResourceOverrideValueKind.FontSize:
                    return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);

                case ResourceOverrideValueKind.FontFamily:
                    return value?.ToString() ?? string.Empty;

                default:
                    return string.Empty;
            }
        }

        private static object FindPlayniteResourceValue(ResourceOverrideDescriptor descriptor)
        {
            var value = Application.Current?.TryFindResource(descriptor.PlayniteResourceKey);
            if (value != null)
            {
                return value;
            }

            foreach (var fallbackKey in descriptor.FallbackPlayniteResourceKeys)
            {
                value = Application.Current?.TryFindResource(fallbackKey);
                if (value != null)
                {
                    return value;
                }
            }

            return null;
        }

        private static string BrushToText(Brush brush)
        {
            if (brush is SolidColorBrush solid)
            {
                var color = solid.Color;
                return $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
            }

            return string.Empty;
        }
    }

    public sealed class RarityAppearanceItem : PlayniteAchievements.Common.ObservableObject
    {
        private readonly PersistedSettings _settings;
        private readonly Action _applyResources;

        public RarityAppearanceItem(
            RarityTier tier,
            PersistedSettings settings,
            Action applyResources)
        {
            Tier = tier;
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _applyResources = applyResources;

            if (_settings.RarityColors == null)
            {
                _settings.RarityColors = RarityColorSettings.CreateDefault();
            }
        }

        public RarityTier Tier { get; }

        public string DisplayName => Tier.ToDisplayText();

        public string BaseColor
        {
            get => GetColor();
            set
            {
                SetColor(value);
                _settings.OnPropertyChanged(nameof(PersistedSettings.RarityColors));
                _applyResources?.Invoke();
                Refresh();
            }
        }

        public Brush PreviewBrush
        {
            get
            {
                try
                {
                    var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(BaseColor));
                    if (brush.CanFreeze)
                    {
                        brush.Freeze();
                    }

                    return brush;
                }
                catch
                {
                    return Brushes.Transparent;
                }
            }
        }

        public ImageSource PreviewBadge =>
            RarityAppearanceHelper.CreateBadgePreview(Tier, _settings);

        public void Refresh()
        {
            OnPropertyChanged(nameof(BaseColor));
            OnPropertyChanged(nameof(PreviewBrush));
            OnPropertyChanged(nameof(PreviewBadge));
        }

        public void Reset()
        {
            SetColor(GetDefaultColor());
            _settings.OnPropertyChanged(nameof(PersistedSettings.RarityColors));
            _applyResources?.Invoke();
            Refresh();
        }

        private string GetColor()
        {
            var colors = _settings.RarityColors ?? RarityColorSettings.CreateDefault();
            switch (Tier)
            {
                case RarityTier.UltraRare:
                    return colors.UltraRare;
                case RarityTier.Rare:
                    return colors.Rare;
                case RarityTier.Uncommon:
                    return colors.Uncommon;
                default:
                    return colors.Common;
            }
        }

        private string GetDefaultColor()
        {
            switch (Tier)
            {
                case RarityTier.UltraRare:
                    return RarityColorSettings.DefaultUltraRare;
                case RarityTier.Rare:
                    return RarityColorSettings.DefaultRare;
                case RarityTier.Uncommon:
                    return RarityColorSettings.DefaultUncommon;
                default:
                    return RarityColorSettings.DefaultCommon;
            }
        }

        private void SetColor(string value)
        {
            if (_settings.RarityColors == null)
            {
                _settings.RarityColors = RarityColorSettings.CreateDefault();
            }

            switch (Tier)
            {
                case RarityTier.UltraRare:
                    _settings.RarityColors.UltraRare = value;
                    break;
                case RarityTier.Rare:
                    _settings.RarityColors.Rare = value;
                    break;
                case RarityTier.Uncommon:
                    _settings.RarityColors.Uncommon = value;
                    break;
                default:
                    _settings.RarityColors.Common = value;
                    break;
            }
        }
    }

    public sealed class RarityPalettePreset
    {
        public RarityPalettePreset(
            string name,
            RarityColorSettings colors,
            IReadOnlyDictionary<string, string> resourceBrushes)
        {
            Name = name;
            Colors = colors ?? RarityColorSettings.CreateDefault();
            ResourceBrushes = resourceBrushes;
        }

        public string Name { get; }

        public RarityColorSettings Colors { get; }

        public IReadOnlyDictionary<string, string> ResourceBrushes { get; }

        public string DisplayLabel { get; set; }

        public Brush CommonBrush => CreateSwatchBrush(Colors.Common);

        public Brush UncommonBrush => CreateSwatchBrush(Colors.Uncommon);

        public Brush RareBrush => CreateSwatchBrush(Colors.Rare);

        public Brush UltraRareBrush => CreateSwatchBrush(Colors.UltraRare);

        private static Brush CreateSwatchBrush(string colorHex)
        {
            try
            {
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorHex));
                if (brush.CanFreeze)
                {
                    brush.Freeze();
                }

                return brush;
            }
            catch
            {
                return Brushes.Transparent;
            }
        }
    }

    public sealed class CompletedBadgeAppearanceItem : PlayniteAchievements.Common.ObservableObject
    {
        private readonly bool _isStartColor;
        private readonly PersistedSettings _settings;
        private readonly Action _applyResources;

        public CompletedBadgeAppearanceItem(
            string displayName,
            bool isStartColor,
            PersistedSettings settings,
            Action applyResources)
        {
            DisplayName = displayName;
            _isStartColor = isStartColor;
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _applyResources = applyResources;

            if (_settings.RarityColors == null)
            {
                _settings.RarityColors = RarityColorSettings.CreateDefault();
            }
        }

        public string DisplayName { get; }

        public string BaseColor
        {
            get => GetColor();
            set
            {
                SetColor(value);
                _settings.OnPropertyChanged(nameof(PersistedSettings.RarityColors));
                _applyResources?.Invoke();
                Refresh();
            }
        }

        public Brush PreviewBrush
        {
            get
            {
                try
                {
                    var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(BaseColor));
                    if (brush.CanFreeze)
                    {
                        brush.Freeze();
                    }

                    return brush;
                }
                catch
                {
                    return Brushes.Transparent;
                }
            }
        }

        public ImageSource PreviewBadge =>
            RarityAppearanceHelper.CreateCompletedBadgePreview(_settings);

        public void Refresh()
        {
            OnPropertyChanged(nameof(BaseColor));
            OnPropertyChanged(nameof(PreviewBrush));
            OnPropertyChanged(nameof(PreviewBadge));
        }

        public void Reset()
        {
            SetColor(_isStartColor
                ? RarityColorSettings.DefaultCompletedStart
                : RarityColorSettings.DefaultCompletedEnd);
            _settings.OnPropertyChanged(nameof(PersistedSettings.RarityColors));
            _applyResources?.Invoke();
            Refresh();
        }

        private string GetColor()
        {
            var colors = _settings.RarityColors ?? RarityColorSettings.CreateDefault();
            return _isStartColor ? colors.CompletedStart : colors.CompletedEnd;
        }

        private void SetColor(string value)
        {
            if (_settings.RarityColors == null)
            {
                _settings.RarityColors = RarityColorSettings.CreateDefault();
            }

            if (_isStartColor)
            {
                _settings.RarityColors.CompletedStart = value;
            }
            else
            {
                _settings.RarityColors.CompletedEnd = value;
            }
        }
    }

    public sealed class TrophyAppearanceItem : PlayniteAchievements.Common.ObservableObject
    {
        private readonly string _trophyKey;
        private readonly PersistedSettings _settings;
        private readonly Action _applyResources;

        public TrophyAppearanceItem(
            string displayName,
            string trophyKey,
            PersistedSettings settings,
            Action applyResources)
        {
            DisplayName = displayName;
            _trophyKey = trophyKey;
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _applyResources = applyResources;

            if (_settings.RarityColors == null)
            {
                _settings.RarityColors = RarityColorSettings.CreateDefault();
            }
        }

        public string DisplayName { get; }

        public string BaseColor
        {
            get => GetColor();
            set
            {
                SetColor(value);
                _settings.OnPropertyChanged(nameof(PersistedSettings.RarityColors));
                _applyResources?.Invoke();
                Refresh();
            }
        }

        public Brush PreviewBrush
        {
            get
            {
                try
                {
                    var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(BaseColor));
                    if (brush.CanFreeze)
                    {
                        brush.Freeze();
                    }

                    return brush;
                }
                catch
                {
                    return Brushes.Transparent;
                }
            }
        }

        public ImageSource PreviewBadge =>
            RarityAppearanceHelper.CreateTrophyPreview(_trophyKey, _settings);

        public void Refresh()
        {
            OnPropertyChanged(nameof(BaseColor));
            OnPropertyChanged(nameof(PreviewBrush));
            OnPropertyChanged(nameof(PreviewBadge));
        }

        public void Reset()
        {
            SetColor(GetDefaultColor());
            _settings.OnPropertyChanged(nameof(PersistedSettings.RarityColors));
            _applyResources?.Invoke();
            Refresh();
        }

        private string GetColor()
        {
            var colors = _settings.RarityColors ?? RarityColorSettings.CreateDefault();
            switch (_trophyKey)
            {
                case "TrophyPlatinum":
                    return colors.TrophyPlatinum;
                case "TrophyGold":
                    return colors.TrophyGold;
                case "TrophySilver":
                    return colors.TrophySilver;
                default:
                    return colors.TrophyBronze;
            }
        }

        private string GetDefaultColor()
        {
            switch (_trophyKey)
            {
                case "TrophyPlatinum":
                    return RarityColorSettings.DefaultTrophyPlatinum;
                case "TrophyGold":
                    return RarityColorSettings.DefaultTrophyGold;
                case "TrophySilver":
                    return RarityColorSettings.DefaultTrophySilver;
                default:
                    return RarityColorSettings.DefaultTrophyBronze;
            }
        }

        private void SetColor(string value)
        {
            if (_settings.RarityColors == null)
            {
                _settings.RarityColors = RarityColorSettings.CreateDefault();
            }

            switch (_trophyKey)
            {
                case "TrophyPlatinum":
                    _settings.RarityColors.TrophyPlatinum = value;
                    break;
                case "TrophyGold":
                    _settings.RarityColors.TrophyGold = value;
                    break;
                case "TrophySilver":
                    _settings.RarityColors.TrophySilver = value;
                    break;
                default:
                    _settings.RarityColors.TrophyBronze = value;
                    break;
            }
        }
    }
}
