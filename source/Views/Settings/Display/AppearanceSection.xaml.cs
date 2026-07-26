using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.UI;
using PlayniteAchievements.ViewModels.Settings;

namespace PlayniteAchievements.Views.Settings.Display
{
    /// <summary>
    /// Display settings: Appearance section. Hosts rarity color, completed badge, trophy and
    /// resource override editors plus palette presets.
    /// </summary>
    public partial class AppearanceSection : UserControl, IDisposable
    {
        private readonly PlayniteAchievementsSettings _settings;
        private readonly Func<Window, string, string> _pickColor;
        private readonly PersistedSettingsSubscription _persistedSubscription;

        private ObservableCollection<ResourceAppearanceItem> _resourceAppearanceItems;
        private ObservableCollection<RarityAppearanceItem> _rarityAppearanceItems;
        private ObservableCollection<CompletedBadgeAppearanceItem> _completedBadgeAppearanceItems;
        private ObservableCollection<TrophyAppearanceItem> _trophyAppearanceItems;
        private ObservableCollection<RarityPalettePreset> _rarityPalettePresets;

        public AppearanceSection()
        {
            InitializeComponent();
        }

        internal AppearanceSection(
            PlayniteAchievementsSettings settings,
            Func<Window, string, string> pickColor)
            : this()
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _pickColor = pickColor ?? throw new ArgumentNullException(nameof(pickColor));

            _persistedSubscription = new PersistedSettingsSubscription(
                _settings,
                OnPersistedPropertyChanged,
                RefreshAppearanceEditorFromPersisted);
        }

        public ObservableCollection<ResourceAppearanceItem> ResourceAppearanceItems
        {
            get
            {
                if (_resourceAppearanceItems == null)
                {
                    _resourceAppearanceItems = new ObservableCollection<ResourceAppearanceItem>();
                    RebuildResourceAppearanceItems();
                }

                return _resourceAppearanceItems;
            }
        }

        public ObservableCollection<RarityAppearanceItem> RarityAppearanceItems
        {
            get
            {
                if (_rarityAppearanceItems == null)
                {
                    _rarityAppearanceItems = new ObservableCollection<RarityAppearanceItem>();
                    RebuildRarityAppearanceItems();
                }

                return _rarityAppearanceItems;
            }
        }

        public ObservableCollection<CompletedBadgeAppearanceItem> CompletedBadgeAppearanceItems
        {
            get
            {
                if (_completedBadgeAppearanceItems == null)
                {
                    _completedBadgeAppearanceItems = new ObservableCollection<CompletedBadgeAppearanceItem>();
                    RebuildCompletedBadgeAppearanceItems();
                }

                return _completedBadgeAppearanceItems;
            }
        }

        public ObservableCollection<TrophyAppearanceItem> TrophyAppearanceItems
        {
            get
            {
                if (_trophyAppearanceItems == null)
                {
                    _trophyAppearanceItems = new ObservableCollection<TrophyAppearanceItem>();
                    RebuildTrophyAppearanceItems();
                }

                return _trophyAppearanceItems;
            }
        }

        public ObservableCollection<RarityPalettePreset> RarityPalettePresets
        {
            get
            {
                if (_rarityPalettePresets == null)
                {
                    _rarityPalettePresets = new ObservableCollection<RarityPalettePreset>(CreateRarityPalettePresets());
                }

                return _rarityPalettePresets;
            }
        }

        private void OnPersistedPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (RarityAppearanceHelper.IsAppearanceSettingPropertyName(e.PropertyName))
            {
                ApplyRarityAppearanceOverrides();
            }
        }

        private void PickResourceColor_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not ResourceAppearanceItem item ||
                !item.IsBrush)
            {
                return;
            }

            var color = _pickColor(Window.GetWindow(this), item.CustomValue);
            if (!string.IsNullOrEmpty(color))
            {
                item.Mode = ResourceOverrideMode.Custom;
                item.CustomValue = color;
            }
        }

        private void PickRarityColor_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is RarityAppearanceItem rarityItem)
            {
                PickPaletteColor(
                    rarityItem.BaseColor,
                    color =>
                    {
                        rarityItem.BaseColor = color;
                    });
                return;
            }

            if ((sender as FrameworkElement)?.DataContext is CompletedBadgeAppearanceItem completedItem)
            {
                PickPaletteColor(
                    completedItem.BaseColor,
                    color =>
                    {
                        completedItem.BaseColor = color;
                    });
                return;
            }

            if ((sender as FrameworkElement)?.DataContext is TrophyAppearanceItem trophyItem)
            {
                PickPaletteColor(
                    trophyItem.BaseColor,
                    color =>
                    {
                        trophyItem.BaseColor = color;
                    });
            }
        }

        private void ResetRarityColor_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is RarityAppearanceItem rarityItem)
            {
                rarityItem.Reset();
                return;
            }

            if ((sender as FrameworkElement)?.DataContext is CompletedBadgeAppearanceItem completedItem)
            {
                completedItem.Reset();
                return;
            }

            if ((sender as FrameworkElement)?.DataContext is TrophyAppearanceItem trophyItem)
            {
                trophyItem.Reset();
            }
        }

        private void ApplySelectedRarityPalettePreset_Click(object sender, RoutedEventArgs e)
        {
            if (RarityPalettePresetComboBox?.SelectedItem is RarityPalettePreset preset)
            {
                ApplyRarityPalette(preset);
            }
        }

        private void ResetAllRarityColors_Click(object sender, RoutedEventArgs e)
        {
            ApplyRarityPalette(new RarityPalettePreset("Default", RarityColorSettings.CreateDefault(), null));
        }

        private void ApplyRarityPalette(RarityPalettePreset preset)
        {
            var persisted = _settings?.Persisted;
            if (persisted == null || preset?.Colors == null)
            {
                return;
            }

            persisted.RarityColors = preset.Colors.Clone();
            persisted.ResourceOverrides = preset.ResourceBrushes != null
                ? CreateResourceOverrideSettings(preset.ResourceBrushes)
                : PersistedSettings.CreateDefaultResourceOverrides();
            RefreshAppearanceEditorFromPersisted();
        }

        private static Dictionary<string, ResourceOverrideSetting> CreateResourceOverrideSettings(
            IReadOnlyDictionary<string, string> brushes)
        {
            var overrides = new Dictionary<string, ResourceOverrideSetting>(StringComparer.OrdinalIgnoreCase);
            if (brushes == null)
            {
                return overrides;
            }

            foreach (var pair in brushes)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value))
                {
                    continue;
                }

                overrides[pair.Key] = new ResourceOverrideSetting
                {
                    Mode = ResourceOverrideMode.Custom,
                    CustomValue = pair.Value.Trim()
                };
            }

            return overrides;
        }

        private static IReadOnlyList<RarityPalettePreset> CreateRarityPalettePresets()
        {
            var presets = new[]
            {
                Preset("Default",
                    RarityColorSettings.DefaultCommon,
                    RarityColorSettings.DefaultUncommon,
                    RarityColorSettings.DefaultRare,
                    RarityColorSettings.DefaultUltraRare,
                    RarityColorSettings.DefaultCompletedStart,
                    RarityColorSettings.DefaultCompletedEnd),

                Preset("Emerald Forest",     "#53633A", "#43A047", "#00875A", "#5E2B97", "#A3E635", "#FDE68A"),
                Preset("Abyssal Ocean",      "#40545A", "#168AAD", "#1D4ED8", "#312E81", "#22D3EE", "#BAE6FD"),
                Preset("Desert Mirage",      "#B08D57", "#2A9D8F", "#E76F51", "#B5179E", "#F4D35E", "#FF9F1C"),
                Preset("Frozen Aurora",      "#9FB3C8", "#67E8F9", "#60A5FA", "#7C3AED", "#34D399", "#F0ABFC"),
                Preset("Volcano Core",       "#5C4033", "#D94A1E", "#F97316", "#7F1D1D", "#FACC15", "#EF4444"),

                Preset("Rose Quartz",        "#9A8C98", "#F4A7B9", "#E85D75", "#6D2E46", "#FFB4A2", "#FFF0E6"),
                Preset("Bone Crypt",         "#A8A29E", "#556B2F", "#A44A3F", "#3B0764", "#F2D492", "#FFF8DC"),
                Preset("Neon City",          "#263238", "#00E5FF", "#FFEA00", "#FF1744", "#AA00FF", "#FF6D00"),
                Preset("Cosmic Nebula",      "#111827", "#14B8A6", "#4F46E5", "#C026D3", "#FB7185", "#22D3EE"),
                Preset("Candy Shop",         "#A7C957", "#7BDFF2", "#FFCB77", "#FF5D8F", "#B388EB", "#FFD6A5"),

                Preset("Noir Spotlight",     "#2F3437", "#9CA3AF", "#F4D35E", "#E63946", "#FFF7C2", "#F72585"),
                Preset("Royal Masquerade",   "#53354A", "#2E8B57", "#1D4ED8", "#C1121F", "#F4D35E", "#FFF1A8"),
                Preset("Autumn Court",       "#5C4033", "#606C38", "#BC6C25", "#780000", "#DDA15E", "#FFD166"),
                Preset("Stormbreaker",       "#4B5563", "#94A3B8", "#FACC15", "#1D4ED8", "#F8FAFC", "#FDE047"),
                Preset("Tidal Gold",         "#B08968", "#84DCC6", "#05668D", "#7B2CBF", "#F4D35E", "#FFF3B0"),

                Preset("Industrial Rust",    "#59636B", "#A65E2E", "#C49A2C", "#1565C0", "#F97316", "#FFE082"),
                Preset("Radioactive Lab",    "#3D3D29", "#A3E635", "#D9F99D", "#7C3AED", "#CCFF00", "#F5FF00"),
                Preset("Sakura Night",       "#353535", "#FFB3C6", "#FF8C42", "#5A189A", "#FFC2D1", "#F8F7FF"),
                Preset("Paper Lantern",      "#8D6E63", "#7CB342", "#E53935", "#3949AB", "#FFD166", "#FFF3B0"),
                Preset("Prismatic Crystal",  "#607D8B", "#4DD0E1", "#7E57C2", "#EC407A", "#B2EBF2", "#FFFFFF")
            };

            for (int i = 0; i < presets.Length; i++)
            {
                presets[i].DisplayLabel = i == 0
                    ? ResourceProvider.GetString("LOCPlayAch_Common_Default")
                    : string.Format(
                        ResourceProvider.GetString("LOCPlayAch_Settings_Appearance_PresetNumbered"),
                        i);
            }

            return presets;
        }

        private static RarityPalettePreset Preset(
            string name,
            string common,
            string uncommon,
            string rare,
            string ultraRare,
            string completedStart,
            string completedEnd)
        {
            return new RarityPalettePreset(
                name,
                new RarityColorSettings
                {
                    Common = common,
                    Uncommon = uncommon,
                    Rare = rare,
                    UltraRare = ultraRare,
                    CompletedStart = completedStart,
                    CompletedEnd = completedEnd,
                    TrophyBronze = common,
                    TrophySilver = uncommon,
                    TrophyGold = rare,
                    TrophyPlatinum = ultraRare
                },
                string.Equals(name, "Default", StringComparison.Ordinal)
                    ? null
                    : CreatePresetResourceBrushes(common, uncommon, rare, ultraRare, completedStart, completedEnd));
        }

        private static IReadOnlyDictionary<string, string> CreatePresetResourceBrushes(
            string common,
            string uncommon,
            string rare,
            string ultraRare,
            string completedStart,
            string completedEnd)
        {
            var baseSurface = "#FF0D1018";
            var basePanel = "#FF141925";
            var baseStrong = "#FF070912";

            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["PlayAch.Brush.Text"] = "#F5F7FB",
                ["PlayAch.Brush.Text.Secondary"] = "#C4CBD8",
                ["PlayAch.Brush.Text.Tertiary"] = "#8792A3",
                ["PlayAch.Brush.WindowSurface"] = BlendColorText(baseStrong, ultraRare, 0.06),
                ["PlayAch.Brush.Surface"] = BlendColorText(baseSurface, common, 0.10),
                ["PlayAch.Brush.GridSurface"] = BlendColorText(baseSurface, rare, 0.10),
                ["PlayAch.Brush.Panel"] = BlendColorText(basePanel, rare, 0.12),
                ["PlayAch.Brush.Border"] = WithAlpha(common, 0xCC),
                ["PlayAch.Brush.ControlBorder"] = WithAlpha(rare, 0xD8),
                ["PlayAch.Brush.Glyph"] = WithAlpha(uncommon, 0xF0),
                ["PlayAch.Brush.Accent"] = WithAlpha(ultraRare, 0xFF),
                ["PlayAch.Brush.Selection"] = WithAlpha(completedStart, 0xFF),
                ["PlayAch.Brush.ControlSurface"] = BlendColorText(basePanel, uncommon, 0.18),
                ["PlayAch.Brush.PopupSurface"] = BlendColorText(basePanel, ultraRare, 0.16),
                ["PlayAch.Brush.PopupBorder"] = WithAlpha(completedEnd, 0xD8)
            };
        }

        private static string BlendColorText(string from, string to, double amount)
        {
            if (!TryParseColor(from, out var fromColor) ||
                !TryParseColor(to, out var toColor))
            {
                return from;
            }

            amount = Math.Max(0, Math.Min(1, amount));
            return ColorToText(Color.FromArgb(
                0xFF,
                (byte)Math.Round(fromColor.R + ((toColor.R - fromColor.R) * amount)),
                (byte)Math.Round(fromColor.G + ((toColor.G - fromColor.G) * amount)),
                (byte)Math.Round(fromColor.B + ((toColor.B - fromColor.B) * amount))));
        }

        private static string WithAlpha(string value, byte alpha)
        {
            return TryParseColor(value, out var color)
                ? ColorToText(Color.FromArgb(alpha, color.R, color.G, color.B))
                : value;
        }

        private static string ColorToText(Color color)
        {
            return $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
        }

        private static bool TryParseColor(string value, out Color color)
        {
            try
            {
                color = (Color)ColorConverter.ConvertFromString(value);
                return true;
            }
            catch
            {
                color = Colors.Transparent;
                return false;
            }
        }

        private void PickPaletteColor(string currentValue, Action<string> applyColor)
        {
            var color = _pickColor(Window.GetWindow(this), currentValue);
            if (!string.IsNullOrEmpty(color))
            {
                applyColor?.Invoke(color);
            }
        }

        private void RebuildResourceAppearanceItems()
        {
            var persisted = _settings?.Persisted;
            if (persisted == null)
            {
                return;
            }

            var items = ResourceAppearanceItems;
            items.Clear();
            foreach (var descriptor in PlayAchResourceService.ResourceDescriptors)
            {
                items.Add(new ResourceAppearanceItem(
                    descriptor,
                    persisted,
                    ApplyResourceAppearanceOverrides));
            }
        }

        private void RebuildRarityAppearanceItems()
        {
            var persisted = _settings?.Persisted;
            if (persisted == null)
            {
                return;
            }

            var items = RarityAppearanceItems;
            items.Clear();
            items.Add(new RarityAppearanceItem(RarityTier.Common, persisted, ApplyRarityAppearanceOverrides));
            items.Add(new RarityAppearanceItem(RarityTier.Uncommon, persisted, ApplyRarityAppearanceOverrides));
            items.Add(new RarityAppearanceItem(RarityTier.Rare, persisted, ApplyRarityAppearanceOverrides));
            items.Add(new RarityAppearanceItem(RarityTier.UltraRare, persisted, ApplyRarityAppearanceOverrides));
        }

        private void RebuildCompletedBadgeAppearanceItems()
        {
            var persisted = _settings?.Persisted;
            if (persisted == null)
            {
                return;
            }

            var items = CompletedBadgeAppearanceItems;
            items.Clear();
            items.Add(new CompletedBadgeAppearanceItem(ResourceProvider.GetString("LOCPlayAch_Settings_Appearance_GradientStart"), true, persisted, ApplyRarityAppearanceOverrides));
            items.Add(new CompletedBadgeAppearanceItem(ResourceProvider.GetString("LOCPlayAch_Settings_Appearance_GradientEnd"), false, persisted, ApplyRarityAppearanceOverrides));
        }

        private void RebuildTrophyAppearanceItems()
        {
            var persisted = _settings?.Persisted;
            if (persisted == null)
            {
                return;
            }

            var items = TrophyAppearanceItems;
            items.Clear();
            items.Add(new TrophyAppearanceItem(ResourceProvider.GetString("LOCPlayAch_Trophy_Bronze"), "TrophyBronze", persisted, ApplyRarityAppearanceOverrides));
            items.Add(new TrophyAppearanceItem(ResourceProvider.GetString("LOCPlayAch_Trophy_Silver"), "TrophySilver", persisted, ApplyRarityAppearanceOverrides));
            items.Add(new TrophyAppearanceItem(ResourceProvider.GetString("LOCPlayAch_Trophy_Gold"), "TrophyGold", persisted, ApplyRarityAppearanceOverrides));
            items.Add(new TrophyAppearanceItem(ResourceProvider.GetString("LOCPlayAch_Trophy_Platinum"), "TrophyPlatinum", persisted, ApplyRarityAppearanceOverrides));
        }

        /// <summary>
        /// Rebuilds all appearance editor items from the current persisted settings and reapplies
        /// resource and rarity overrides to the application resources.
        /// </summary>
        public void RefreshAppearanceEditorFromPersisted()
        {
            RebuildResourceAppearanceItems();
            RebuildRarityAppearanceItems();
            RebuildCompletedBadgeAppearanceItems();
            RebuildTrophyAppearanceItems();
            ApplyResourceAppearanceOverrides();
            ApplyRarityAppearanceOverrides();
        }

        private void ApplyResourceAppearanceOverrides()
        {
            var resources = Application.Current?.Resources;
            if (resources != null)
            {
                PlayAchResourceService.Apply(
                    resources,
                    _settings?.Persisted?.ResourceOverrides,
                    _settings?.Persisted);
            }
        }

        private void ApplyRarityAppearanceOverrides()
        {
            RarityAppearanceHelper.ApplyBadgeApplicationResources(
                _settings?.Persisted);
            RefreshRarityAppearanceItems();
        }

        private void RefreshRarityAppearanceItems()
        {
            if (_rarityAppearanceItems != null)
            {
                foreach (var item in _rarityAppearanceItems)
                {
                    item.Refresh();
                }
            }

            if (_completedBadgeAppearanceItems != null)
            {
                foreach (var item in _completedBadgeAppearanceItems)
                {
                    item.Refresh();
                }
            }

            if (_trophyAppearanceItems != null)
            {
                foreach (var item in _trophyAppearanceItems)
                {
                    item.Refresh();
                }
            }
        }

        public void Dispose()
        {
            _persistedSubscription?.Dispose();
        }
    }
}
