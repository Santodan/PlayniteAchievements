using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.Metadata;
using ObservableObject = PlayniteAchievements.Common.ObservableObject;

namespace PlayniteAchievements.Views.Settings.Metadata
{
    /// <summary>
    /// Metadata settings: the Sources page. One grid whose first row holds the defaults and whose
    /// other rows override them per platform.
    /// </summary>
    public partial class MetadataSourcesSection : UserControl
    {
        public MetadataSourcesSection()
        {
            InitializeComponent();
        }

        internal MetadataSourcesSection(PlayniteAchievementsSettings settings, ProviderRegistry registry)
            : this()
        {
            DataContext = new MetadataSourcesViewModel(
                settings ?? throw new ArgumentNullException(nameof(settings)),
                registry ?? throw new ArgumentNullException(nameof(registry)));
        }

        private void TestIconMatching_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            PlayniteAchievementsPlugin.Instance?.RunIconMatchTest();
        }
    }

    public sealed class MetadataSourcesViewModel
    {
        internal MetadataSourcesViewModel(PlayniteAchievementsSettings settings, ProviderRegistry registry)
        {
            var providers = registry.GetMetadataProviders() ?? Array.Empty<IMetadataProvider>();
            var rows = new List<MetadataChoiceRow> { new MetadataChoiceRow(settings, providers, null) };
            rows.AddRange((registry.GetSettingsViewProviderKeys() ?? Enumerable.Empty<string>())
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Select(key => new MetadataChoiceRow(settings, providers, key)));
            Rows = rows;
        }

        public IReadOnlyList<MetadataChoiceRow> Rows { get; }
    }

    /// <summary>
    /// One grid row. Cells hold option keys: <see cref="DefaultKey"/> follows the defaults row, an
    /// empty key is the platform's own data, and categories carry their mode as "Source|Mode".
    /// </summary>
    public sealed class MetadataChoiceRow : ObservableObject
    {
        public const string DefaultKey = "~";
        private const char ModeSeparator = '|';

        private readonly PlayniteAchievementsSettings _settings;
        private string _icons;
        private string _categories;
        private string _rarity;
        private string _capstoneArt;

        internal MetadataChoiceRow(
            PlayniteAchievementsSettings settings,
            IReadOnlyList<IMetadataProvider> providers,
            string providerKey)
        {
            _settings = settings;
            ProviderKey = providerKey;
            IsDefaults = providerKey == null;
            DisplayName = IsDefaults ? ResourceProvider.GetString("LOCDefault") : ProviderRegistry.GetLocalizedName(providerKey);
            if (!IsDefaults)
            {
                ProviderRegistry.TryResolveProviderVisuals(providerKey, out var iconKey, out _);
                ProviderIconKey = iconKey;
            }

            SupportsRarity = IsDefaults || MetadataChoices.ExophaseRarityProviders.Contains(providerKey);
            IconOptions = BuildSourceOptions(providers, MetadataField.Icons, "LOCPlayAch_Settings_Metadata_Source_Own");
            CapstoneArtOptions = BuildSourceOptions(providers, MetadataField.CapstoneArt, "LOCPlayAch_Settings_Metadata_CapstoneArt_GameIcon");
            CategoryOptions = BuildCategoryOptions(providers);
            RarityOptions = WithDefault(new[]
            {
                new ChoiceOption(string.Empty, ResourceProvider.GetString("LOCPlayAch_Settings_Metadata_Source_Own")),
                new ChoiceOption(MetadataChoices.Exophase, ResourceProvider.GetString("LOCPlayAch_Provider_Exophase"))
            });

            var stored = IsDefaults
                ? settings.Persisted?.MetadataDefaults ?? MetadataChoices.CreateDefaults()
                : FindOverride(settings.Persisted, providerKey);
            _icons = ToKey(stored?.Icons);
            _categories = ToCategoryKey(stored);
            _rarity = SupportsRarity ? ToKey(stored?.Rarity) : DefaultKey;
            _capstoneArt = ToKey(stored?.CapstoneArt);
        }

        public string ProviderKey { get; }

        public bool IsDefaults { get; }

        public string DisplayName { get; }

        public string ProviderIconKey { get; }

        public string ProviderColorHex => IsDefaults ? null : ProviderRegistry.GetProviderColorHex(ProviderKey);

        public bool HasProviderIcon => !string.IsNullOrWhiteSpace(ProviderIconKey);

        public bool SupportsRarity { get; }

        public IReadOnlyList<ChoiceOption> IconOptions { get; }

        public IReadOnlyList<ChoiceOption> CategoryOptions { get; }

        public IReadOnlyList<ChoiceOption> RarityOptions { get; }

        public IReadOnlyList<ChoiceOption> CapstoneArtOptions { get; }

        public string Icons
        {
            get => _icons;
            set { _icons = value; Save(); OnPropertyChanged(); }
        }

        public string Categories
        {
            get => _categories;
            set { _categories = value; Save(); OnPropertyChanged(); }
        }

        public string Rarity
        {
            get => _rarity;
            set { _rarity = value; Save(); OnPropertyChanged(); }
        }

        public string CapstoneArt
        {
            get => _capstoneArt;
            set { _capstoneArt = value; Save(); OnPropertyChanged(); }
        }

        private void Save()
        {
            var persisted = _settings.Persisted;
            if (persisted == null)
            {
                return;
            }

            var choices = new MetadataChoices
            {
                Icons = FromKey(_icons),
                Rarity = SupportsRarity ? FromKey(_rarity) : null,
                CapstoneArt = FromKey(_capstoneArt)
            };
            ApplyCategoryKey(choices, _categories);

            if (IsDefaults)
            {
                var defaults = MetadataChoices.CreateDefaults();
                persisted.MetadataDefaults = new MetadataChoices
                {
                    Icons = choices.Icons ?? defaults.Icons,
                    Categories = choices.Categories ?? defaults.Categories,
                    CategoryMode = choices.CategoryMode ?? defaults.CategoryMode,
                    Rarity = choices.Rarity ?? defaults.Rarity,
                    CapstoneArt = choices.CapstoneArt ?? defaults.CapstoneArt
                };
                return;
            }

            var platforms = PersistedSettings.ClonePlatformChoices(persisted.MetadataPlatformChoices);
            platforms[ProviderKey] = choices;
            persisted.MetadataPlatformChoices = platforms;
        }

        private IReadOnlyList<ChoiceOption> BuildSourceOptions(
            IReadOnlyList<IMetadataProvider> providers,
            MetadataField field,
            string ownLabelKey)
        {
            var options = new List<ChoiceOption> { new ChoiceOption(string.Empty, ResourceProvider.GetString(ownLabelKey)) };
            options.AddRange(providers
                .Where(provider => (provider.SupportedFields & field) == field &&
                                   (IsDefaults || !provider.IsNativeFor(ProviderKey)))
                .Select(provider => new ChoiceOption(provider.Key, ResourceProvider.GetString(provider.DisplayNameKey))));
            return WithDefault(options);
        }

        private IReadOnlyList<ChoiceOption> BuildCategoryOptions(IReadOnlyList<IMetadataProvider> providers)
        {
            var format = ResourceProvider.GetString("LOCPlayAch_Settings_Metadata_SourceMode");
            var merge = ResourceProvider.GetString("LOCPlayAch_Common_Merge");
            var replace = ResourceProvider.GetString("LOCPlayAch_Settings_SteamHuntersCategories_Override");
            var options = new List<ChoiceOption>
            {
                new ChoiceOption(string.Empty, ResourceProvider.GetString("LOCPlayAch_Settings_Override_Off"))
            };
            foreach (var provider in providers.Where(provider => (provider.SupportedFields & MetadataField.Categories) != 0))
            {
                var name = ResourceProvider.GetString(provider.DisplayNameKey);
                options.Add(new ChoiceOption(provider.Key + ModeSeparator + MetadataCategoryMode.Merge, string.Format(format, name, merge)));
                options.Add(new ChoiceOption(provider.Key + ModeSeparator + MetadataCategoryMode.Override, string.Format(format, name, replace)));
            }

            return WithDefault(options);
        }

        private IReadOnlyList<ChoiceOption> WithDefault(IEnumerable<ChoiceOption> options)
        {
            return IsDefaults
                ? options.ToList()
                : new[] { new ChoiceOption(DefaultKey, ResourceProvider.GetString("LOCDefault")) }.Concat(options).ToList();
        }

        private static MetadataChoices FindOverride(PersistedSettings persisted, string providerKey)
        {
            MetadataChoices choices = null;
            persisted?.MetadataPlatformChoices?.TryGetValue(providerKey, out choices);
            return choices;
        }

        private static string ToKey(string value) => value ?? DefaultKey;

        private static string FromKey(string key) => key == null || key == DefaultKey ? null : key;

        private static string ToCategoryKey(MetadataChoices stored)
        {
            if (stored?.Categories == null)
            {
                return DefaultKey;
            }

            return stored.Categories.Length == 0
                ? string.Empty
                : stored.Categories + ModeSeparator + (stored.CategoryMode ?? MetadataCategoryMode.Merge);
        }

        private static void ApplyCategoryKey(MetadataChoices choices, string key)
        {
            if (key == null || key == DefaultKey)
            {
                return;
            }

            var parts = key.Split(ModeSeparator);
            choices.Categories = parts[0];
            choices.CategoryMode = parts.Length > 1 && Enum.TryParse(parts[1], out MetadataCategoryMode mode)
                ? mode
                : MetadataCategoryMode.Merge;
        }
    }

    public sealed class ChoiceOption
    {
        public ChoiceOption(string key, string label)
        {
            Key = key;
            Label = string.IsNullOrWhiteSpace(label) ? key : label;
        }

        public string Key { get; }

        public string Label { get; }
    }
}
