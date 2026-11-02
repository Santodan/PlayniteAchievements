using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
// WinForms dialogs: the WPF Microsoft.Win32 pickers render legacy-style on .NET Framework.
using DialogResult = System.Windows.Forms.DialogResult;
using OpenFileDialog = System.Windows.Forms.OpenFileDialog;
using SaveFileDialog = System.Windows.Forms.SaveFileDialog;
using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Services.Library;
using PlayniteAchievements.Services.UI;
using PlayniteAchievements.Services.Workshop;
using PlayniteAchievements.ViewModels.Settings;
using PlayniteAchievements.Views.Dialogs;
using PlayniteAchievements.Views.Helpers;
using PlayniteAchievements.Views.Settings.Controls;

namespace PlayniteAchievements.Views.Settings.Display
{
    /// <summary>
    /// Display settings: Appearance section. Hosts rarity color, completed badge, trophy and
    /// resource override editors plus palette presets.
    /// </summary>
    public partial class ColorsSection : UserControl, IDisposable
    {
        private readonly PlayniteAchievementsSettings _settings;
        private readonly PlayniteAchievementsPlugin _plugin;
        private readonly ProviderRegistry _providerRegistry;
        private readonly ILogger _logger;
        private readonly Func<Window, string, string> _pickColor;
        private readonly PersistedSettingsSubscription _persistedSubscription;

        private ObservableCollection<ResourceAppearanceItem> _resourceAppearanceItems;
        private ObservableCollection<RarityAppearanceItem> _rarityAppearanceItems;
        private ObservableCollection<CompletedBadgeAppearanceItem> _completedBadgeAppearanceItems;
        private ObservableCollection<TrophyAppearanceItem> _trophyAppearanceItems;
        private ObservableCollection<ProviderAppearanceItem> _providerAppearanceItems;

        public ColorsSection()
        {
            InitializeComponent();
        }

        internal ColorsSection(
            PlayniteAchievementsSettings settings,
            PlayniteAchievementsPlugin plugin,
            ILogger logger,
            Func<Window, string, string> pickColor)
            : this()
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            _providerRegistry = plugin.ProviderRegistry ?? throw new ArgumentNullException(nameof(plugin.ProviderRegistry));
            _logger = logger;
            _pickColor = pickColor ?? throw new ArgumentNullException(nameof(pickColor));

            _persistedSubscription = new PersistedSettingsSubscription(
                _settings,
                OnPersistedPropertyChanged,
                RefreshAppearanceEditorFromPersisted);

            ColorSetPresetPicker.Initialize(new LibraryPresetPickerOptions
            {
                Plugin = plugin,
                Settings = settings,
                Adapter = plugin.ColorsLibraryAdapter,
                Presets = plugin.ColorPresetStore,
                ExportCurrent = path => plugin.ColorPackPortableStore.Export(_settings.Persisted, path),
                TargetChanged = RefreshAppearanceEditorFromPersisted,
                NestedValue = () => _settings.Persisted?.RarityColors,
                BuiltIns = CreateRarityPalettePresets().Select(ToBuiltIn).ToList(),
                ItemSwatches = ColorSetSwatches,
                Logger = logger
            });
        }

        /// <summary>
        /// A built-in palette as an entry of the one preset list: picking it writes its rarity
        /// colors (and resource defaults) and ends any color set the colors followed.
        /// </summary>
        private LibraryPickerBuiltIn ToBuiltIn(RarityPalettePreset preset)
        {
            return new LibraryPickerBuiltIn
            {
                Label = preset.DisplayLabel,
                Swatches = SwatchesOf(preset),
                Apply = () =>
                {
                    ApplyRarityPalette(preset);
                    LibraryApplyService.StopFollowing(_plugin.ColorsLibraryAdapter, _settings.Persisted);
                },
                IsCurrent = () => SameRarityColors(preset.Colors, _settings?.Persisted?.RarityColors)
            };
        }

        private static IReadOnlyList<Brush> SwatchesOf(RarityPalettePreset preset)
        {
            return new[] { preset.CommonBrush, preset.UncommonBrush, preset.RareBrush, preset.UltraRareBrush };
        }

        // Swatches of a library color set, read from its file once per file version.
        private readonly Dictionary<string, IReadOnlyList<Brush>> _colorSetSwatches =
            new Dictionary<string, IReadOnlyList<Brush>>(StringComparer.OrdinalIgnoreCase);

        private IReadOnlyList<Brush> ColorSetSwatches(LibraryItem item)
        {
            var path = _plugin?.LibraryStore?.FullPath(item);
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
            {
                return null;
            }

            var key = path + "|" + System.IO.File.GetLastWriteTimeUtc(path).Ticks;
            if (!_colorSetSwatches.TryGetValue(key, out var swatches))
            {
                var colors = _plugin.ColorPackPortableStore.Read(path)?.RarityColors;
                swatches = colors == null ? null : SwatchesOf(new RarityPalettePreset(item.Name, colors, null));
                _colorSetSwatches[key] = swatches;
            }

            return swatches;
        }

        private static bool SameRarityColors(RarityColorSettings left, RarityColorSettings right)
        {
            return left != null && right != null
                   && Newtonsoft.Json.JsonConvert.SerializeObject(left) == Newtonsoft.Json.JsonConvert.SerializeObject(right);
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


        public ObservableCollection<ProviderAppearanceItem> ProviderAppearanceItems
        {
            get
            {
                if (_providerAppearanceItems == null)
                {
                    _providerAppearanceItems = new ObservableCollection<ProviderAppearanceItem>();
                    RebuildProviderAppearanceItems();
                }

                return _providerAppearanceItems;
            }
        }

        private void OnPersistedPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            // The plugin's settings handler applies the badge resources app-wide; only the
            // editor previews need refreshing here.
            if (RarityAppearanceHelper.IsAppearanceSettingPropertyName(e.PropertyName))
            {
                RefreshRarityAppearanceItems();
            }

            if (e.PropertyName == nameof(PersistedSettings.ProviderColorOverrides))
            {
                RefreshProviderAppearanceItems();
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

        private void PickProviderColor_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is ProviderAppearanceItem item)
            {
                PickPaletteColor(item.BaseColor, color => item.BaseColor = color);
            }
        }

        private void ResetProviderColor_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is ProviderAppearanceItem item)
            {
                item.Reset();
            }
        }

        private void ResetAllProviderColors_Click(object sender, RoutedEventArgs e)
        {
            var persisted = _settings?.Persisted;
            if (persisted == null)
            {
                return;
            }

            persisted.ProviderColorOverrides =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            RefreshProviderAppearanceItems();
        }

        /// <summary>Writes the current colors (rarity, provider, resource overrides) to a .pacolors file.</summary>
        private void ExportColors_Click(object sender, RoutedEventArgs e)
        {
            WorkshopMenus.OpenExport(
                sender as Button,
                () => ExportColorsFile_Click(sender, e),
                () => _plugin.OpenWorkshopShare(WorkshopItemKind.Colors, Window.GetWindow(this)));
        }

        private void ExportColorsFile_Click(object sender, RoutedEventArgs e)
        {
            var persisted = _settings?.Persisted;
            var store = _plugin?.ColorPackPortableStore;
            if (persisted == null || store == null)
            {
                return;
            }

            try
            {
                var dialog = new SaveFileDialog
                {
                    Filter = ColorPackPortableStore.BuildFileDialogFilter(),
                    AddExtension = true,
                    DefaultExt = ColorPackPortableStore.PackageFileExtension,
                    FileName = "colors" + ColorPackPortableStore.PackageFileExtension
                };

                if (dialog.ShowDialog() != DialogResult.OK)
                {
                    return;
                }

                store.Export(persisted, ColorPackPortableStore.NormalizeExportPath(dialog.FileName));
                ShowMessage(ResourceProvider.GetString("LOCPlayAch_Status_Succeeded"), MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed exporting colors.");
                ShowMessage(string.Format(ResourceProvider.GetString("LOCPlayAch_Status_Failed"), ex.Message), MessageBoxImage.Error);
            }
        }

        /// <summary>Replaces the current colors with a .pacolors file's, then refreshes the live resources.</summary>
        private void ImportColors_Click(object sender, RoutedEventArgs e)
        {
            WorkshopMenus.OpenImport(
                sender as Button,
                () => ImportColorsFile_Click(sender, e),
                () => _plugin.OpenWorkshopWindow(focusKind: WorkshopItemKind.Colors));
        }

        /// <summary>
        /// Adds a .pacolors file to the library as a local color set, named after the file.
        /// Applying it is the job of the set list, so the current colors do not change here.
        /// </summary>
        private void ImportColorsFile_Click(object sender, RoutedEventArgs e)
        {
            var presets = _plugin?.ColorPresetStore;
            if (presets == null)
            {
                return;
            }

            try
            {
                var dialog = new OpenFileDialog
                {
                    Filter = ColorPackPortableStore.BuildFileDialogFilter(),
                    CheckFileExists = true,
                    Multiselect = false
                };

                if (dialog.ShowDialog() != DialogResult.OK)
                {
                    return;
                }

                var saved = presets.SaveFrom(presets.UniqueName(PackageStem(dialog.FileName)), dialog.FileName);
                ColorSetPresetPicker.AddLocalPreset(saved);
                ShowMessage(string.Format(ResourceProvider.GetString("LOCPlayAch_Workshop_SavedAsPreset"), saved.Name), MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed importing colors.");
                ShowMessage(string.Format(ResourceProvider.GetString("LOCPlayAch_Status_Failed"), ex.Message), MessageBoxImage.Error);
            }
        }

        /// <summary>The file name without its package extension, including a trailing .zip.</summary>
        private static string PackageStem(string path)
        {
            var name = System.IO.Path.GetFileName(path) ?? string.Empty;
            foreach (var suffix in new[] { ".zip", ColorPackPortableStore.PackageFileExtension, BundlePortableStore.PackageFileExtension })
            {
                if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    name = name.Substring(0, name.Length - suffix.Length);
                }
            }

            return name;
        }

        private AchievementToastTemplateResolver CreateTemplateResolver()
        {
            return new AchievementToastTemplateResolver(
                _plugin.PlayniteApi,
                _logger,
                customTemplatesDirectory: AchievementToastTemplateResolver.GetCustomTemplatesDirectory(
                    _plugin.GetPluginUserDataPath()));
        }

        private void ShowMessage(string message, MessageBoxImage image)
        {
            _plugin?.PlayniteApi?.Dialogs?.ShowMessage(
                message,
                ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                MessageBoxButton.OK,
                image);
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
            items.Add(new RarityAppearanceItem(RarityTier.Common, persisted, RefreshRarityAppearanceItems));
            items.Add(new RarityAppearanceItem(RarityTier.Uncommon, persisted, RefreshRarityAppearanceItems));
            items.Add(new RarityAppearanceItem(RarityTier.Rare, persisted, RefreshRarityAppearanceItems));
            items.Add(new RarityAppearanceItem(RarityTier.UltraRare, persisted, RefreshRarityAppearanceItems));
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
            items.Add(new CompletedBadgeAppearanceItem(ResourceProvider.GetString("LOCPlayAch_Settings_Appearance_GradientStart"), true, persisted, RefreshRarityAppearanceItems));
            items.Add(new CompletedBadgeAppearanceItem(ResourceProvider.GetString("LOCPlayAch_Settings_Appearance_GradientEnd"), false, persisted, RefreshRarityAppearanceItems));
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
            items.Add(new TrophyAppearanceItem(ResourceProvider.GetString("LOCPlayAch_Trophy_Bronze"), "TrophyBronze", persisted, RefreshRarityAppearanceItems));
            items.Add(new TrophyAppearanceItem(ResourceProvider.GetString("LOCPlayAch_Trophy_Silver"), "TrophySilver", persisted, RefreshRarityAppearanceItems));
            items.Add(new TrophyAppearanceItem(ResourceProvider.GetString("LOCPlayAch_Trophy_Gold"), "TrophyGold", persisted, RefreshRarityAppearanceItems));
            items.Add(new TrophyAppearanceItem(ResourceProvider.GetString("LOCPlayAch_Trophy_Platinum"), "TrophyPlatinum", persisted, RefreshRarityAppearanceItems));
        }

        private void RebuildProviderAppearanceItems()
        {
            var persisted = _settings?.Persisted;
            if (persisted == null || _providerRegistry == null)
            {
                return;
            }

            var items = ProviderAppearanceItems;
            items.Clear();
            foreach (var provider in _providerRegistry.GetAllProviders())
            {
                items.Add(new ProviderAppearanceItem(
                    provider,
                    persisted,
                    RefreshProviderAppearanceItems));
            }
        }

        /// <summary>
        /// Rebuilds all appearance editor items from the current persisted settings and reapplies
        /// resource overrides to the application resources. Badge resources are applied by the
        /// plugin when the rarity settings change or the persisted instance is replaced.
        /// </summary>
        public void RefreshAppearanceEditorFromPersisted()
        {
            RebuildResourceAppearanceItems();
            RebuildRarityAppearanceItems();
            RebuildCompletedBadgeAppearanceItems();
            RebuildTrophyAppearanceItems();
            RebuildProviderAppearanceItems();
            ApplyResourceAppearanceOverrides();
            RefreshProviderAppearanceItems();
        }

        private void ApplyResourceAppearanceOverrides()
        {
            PlayAchResourceService.ApplyToApplication(
                _settings?.Persisted?.ResourceOverrides,
                _settings?.Persisted);
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

        private void RefreshProviderAppearanceItems()
        {
            if (_providerAppearanceItems == null)
            {
                return;
            }

            foreach (var item in _providerAppearanceItems)
            {
                item.Refresh();
            }
        }

        public void Dispose()
        {
            _persistedSubscription?.Dispose();
            ColorSetPresetPicker?.Dispose();
        }
    }
}
