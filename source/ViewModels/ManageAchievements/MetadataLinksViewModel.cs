using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using Playnite.SDK;
using PlayniteAchievements.Providers.Metadata;
using PlayniteAchievements.Services.Metadata;
using ObservableObject = PlayniteAchievements.Common.ObservableObject;
using RelayCommand = PlayniteAchievements.Common.RelayCommand;

namespace PlayniteAchievements.ViewModels.ManageAchievements
{
    /// <summary>
    /// Manage Achievements Overview: one row per metadata source the game can use, showing the
    /// linked id and whether it was matched or entered, with an editable box. Applying an id pins
    /// it; applying an empty box stops matching; clearing lets matching run again on the next refresh.
    /// </summary>
    public sealed class MetadataLinksViewModel : ObservableObject
    {
        private readonly Func<MetadataLinkStore> _store;
        private readonly Func<IReadOnlyList<IMetadataProvider>> _providers;
        private readonly Action _onChanged;

        internal MetadataLinksViewModel(
            Func<MetadataLinkStore> store,
            Func<IReadOnlyList<IMetadataProvider>> providers,
            Action onChanged)
        {
            _store = store;
            _providers = providers;
            _onChanged = onChanged;
        }

        public ObservableCollection<MetadataLinkRow> Rows { get; } = new ObservableCollection<MetadataLinkRow>();

        public bool HasRows => Rows.Count > 0;

        internal void Load(Guid gameId, string servicingProviderKey)
        {
            Rows.Clear();
            var store = _store?.Invoke();
            if (gameId != Guid.Empty && store != null)
            {
                foreach (var provider in (_providers?.Invoke() ?? Array.Empty<IMetadataProvider>())
                    .Where(provider => !provider.IsNativeFor(servicingProviderKey)))
                {
                    store.TryGet(gameId, provider.Key, out var record);
                    Rows.Add(new MetadataLinkRow(
                        this,
                        gameId,
                        provider.Key,
                        ResourceProvider.GetString(provider.DisplayNameKey),
                        record,
                        store.GetCoverage(gameId, provider.Key)));
                }
            }

            OnPropertyChanged(nameof(HasRows));
        }

        internal void Apply(MetadataLinkRow row)
        {
            _store?.Invoke()?.SetUser(row.GameId, row.SourceKey, row.Input);
            _onChanged?.Invoke();
        }

        internal void Clear(MetadataLinkRow row)
        {
            _store?.Invoke()?.Clear(row.GameId, row.SourceKey);
            _onChanged?.Invoke();
        }
    }

    public sealed class MetadataLinkRow : ObservableObject
    {
        private string _input;

        internal MetadataLinkRow(
            MetadataLinksViewModel owner,
            Guid gameId,
            string sourceKey,
            string sourceName,
            MetadataLinkRecord record,
            MetadataCoverage coverage)
        {
            GameId = gameId;
            SourceKey = sourceKey;
            SourceName = string.IsNullOrWhiteSpace(sourceName) ? sourceKey : sourceName;
            _input = record?.Id ?? string.Empty;
            StatusText = BuildStatus(record, coverage);
            HasLink = record != null;
            ApplyCommand = new RelayCommand(_ => owner.Apply(this));
            ClearCommand = new RelayCommand(_ => owner.Clear(this), _ => HasLink);
        }

        public Guid GameId { get; }

        public string SourceKey { get; }

        public string SourceName { get; }

        public string StatusText { get; }

        public bool HasLink { get; }

        public string Input
        {
            get => _input;
            set => SetValue(ref _input, value);
        }

        public ICommand ApplyCommand { get; }

        public ICommand ClearCommand { get; }

        private static string BuildStatus(MetadataLinkRecord record, MetadataCoverage coverage)
        {
            if (record == null || record.IsNoMatch)
            {
                return ResourceProvider.GetString("LOCPlayAch_Common_Status_NotLinked");
            }

            var origin = record.Origin == MetadataLinkOrigin.User
                ? ResourceProvider.GetString("LOCPlayAch_ManageAchievements_Overview_MetadataLink_SetByYou")
                : ResourceProvider.GetString("LOCAutomatic");
            var parts = new List<string>
            {
                string.IsNullOrWhiteSpace(record.Label) ? record.Id : $"{record.Label} ({record.Id})",
                origin
            };

            // Absent until the next refresh runs the source for this game.
            if (coverage != null && coverage.Total > 0)
            {
                parts.Add(string.Format(
                    ResourceProvider.GetString("LOCPlayAch_ManageAchievements_Overview_MetadataLink_Matched"),
                    coverage.Matched,
                    coverage.Total));
            }

            return string.Join(" · ", parts);
        }
    }
}
