using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.Metadata;
using PlayniteAchievements.Providers.Settings;
using ObservableObject = PlayniteAchievements.Common.ObservableObject;

namespace PlayniteAchievements.Views.Settings.Metadata
{
    /// <summary>
    /// Metadata settings tab: which secondary sources supply icons, categories, rarity and capstone
    /// art to games another platform services, and whether sources may match games by name.
    /// Provider-owned settings (SteamHunters mode, Exophase rarity) are edited through the provider
    /// registry's edit session, the same copies the platform pages use.
    /// </summary>
    public partial class MetadataSettingsTab : UserControl
    {
        public MetadataSettingsTab()
        {
            InitializeComponent();
        }

        internal MetadataSettingsTab(PlayniteAchievementsSettings settings, ProviderRegistry registry)
            : this()
        {
            DataContext = new MetadataSettingsViewModel(
                settings ?? throw new ArgumentNullException(nameof(settings)),
                registry ?? throw new ArgumentNullException(nameof(registry)));
        }
    }

    public sealed class MetadataSettingsViewModel : ObservableObject
    {
        // Platforms whose scanners can take rarity from Exophase.
        private static readonly string[] ExophaseRarityProviderKeys =
        {
            "Xbox", "Xenia", "EA", "Ubisoft", "RPCS3", "ShadPS4", "Vita3K"
        };

        private readonly PlayniteAchievementsSettings _settings;

        internal MetadataSettingsViewModel(PlayniteAchievementsSettings settings, ProviderRegistry registry)
        {
            _settings = settings;

            var providers = registry.GetMetadataProviders();
            IconSourceOptions = BuildOptions(providers, MetadataField.Icons);
            CapstoneArtOptions = BuildOptions(providers, MetadataField.CapstoneArt, "LOCPlayAch_Settings_Metadata_CapstoneArt_GameIcon");

            Steam = registry.GetSettingsForEdit("Steam");
            ExophaseRarityRows = ExophaseRarityProviderKeys
                .Select(key => new ProviderToggleRow(
                    registry.TryGetProvider(key, out var provider) ? provider.ProviderName : key,
                    registry.GetSettingsForEdit(key)))
                .Where(row => row.Settings != null)
                .ToList();
        }

        public PersistedSettings Persisted => _settings.Persisted;

        public IReadOnlyList<SourceOption> IconSourceOptions { get; }

        public IReadOnlyList<SourceOption> CapstoneArtOptions { get; }

        /// <summary>The Steam provider's edit copy; its SteamHunters mode applies to every platform.</summary>
        public IProviderSettings Steam { get; }

        public IReadOnlyList<ProviderToggleRow> ExophaseRarityRows { get; }

        /// <summary>The first preferred icon source, or empty for each platform's own icons.</summary>
        public string SelectedIconSource
        {
            get => Persisted?.MetadataIconSources?.FirstOrDefault() ?? string.Empty;
            set
            {
                if (Persisted == null)
                {
                    return;
                }

                Persisted.MetadataIconSources = ToList(value);
                OnPropertyChanged();
            }
        }

        public string SelectedCapstoneArtSource
        {
            get => Persisted?.MetadataCapstoneArtSources?.FirstOrDefault() ?? string.Empty;
            set
            {
                if (Persisted == null)
                {
                    return;
                }

                Persisted.MetadataCapstoneArtSources = ToList(value);
                OnPropertyChanged();
            }
        }

        private static List<string> ToList(string key)
        {
            return string.IsNullOrWhiteSpace(key) ? new List<string>() : new List<string> { key.Trim() };
        }

        private static IReadOnlyList<SourceOption> BuildOptions(
            IEnumerable<IMetadataProvider> providers,
            MetadataField field,
            string ownLabelKey = "LOCPlayAch_Settings_Metadata_Source_Own")
        {
            var options = new List<SourceOption> { new SourceOption(string.Empty, ResourceProvider.GetString(ownLabelKey)) };
            options.AddRange((providers ?? Enumerable.Empty<IMetadataProvider>())
                .Where(provider => (provider.SupportedFields & field) == field)
                .Select(provider => new SourceOption(provider.Key, ResourceProvider.GetString(provider.DisplayNameKey))));
            return options;
        }
    }

    public sealed class SourceOption
    {
        public SourceOption(string key, string label)
        {
            Key = key;
            Label = string.IsNullOrWhiteSpace(label) ? key : label;
        }

        public string Key { get; }

        public string Label { get; }
    }

    /// <summary>One platform's checkbox, bound by name to its settings edit copy's toggle.</summary>
    public sealed class ProviderToggleRow
    {
        public ProviderToggleRow(string label, IProviderSettings settings)
        {
            Label = label;
            Settings = settings;
        }

        public string Label { get; }

        public IProviderSettings Settings { get; }
    }
}
