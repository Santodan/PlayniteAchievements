using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Providers.Settings;
using PlayniteAchievements.Services;

namespace PlayniteAchievements.Providers.Manual
{
    public partial class ManualSettingsView : ProviderSettingsViewBase
    {
        private const string SuccessStoryExtensionId = "cebe6d32-8c46-4459-b993-5a5189d60788";
        private readonly IPlayniteAPI _playniteApi;
        private readonly ILogger _logger;
        private readonly PlayniteAchievementsSettings _pluginSettings;
        private ManualSettings _manualSettings;

        public static readonly DependencyProperty LegacyImportPathProperty =
            DependencyProperty.Register(
                nameof(LegacyImportPath),
                typeof(string),
                typeof(ManualSettingsView),
                new PropertyMetadata(string.Empty));

        public string LegacyImportPath
        {
            get => (string)GetValue(LegacyImportPathProperty);
            set => SetValue(LegacyImportPathProperty, value);
        }

        public static readonly DependencyProperty LegacyImportStatusProperty =
            DependencyProperty.Register(
                nameof(LegacyImportStatus),
                typeof(string),
                typeof(ManualSettingsView),
                new PropertyMetadata(ResourceProvider.GetString("LOCPlayAch_Settings_Manual_Legacy_StatusIdle")));

        public string LegacyImportStatus
        {
            get => (string)GetValue(LegacyImportStatusProperty);
            set => SetValue(LegacyImportStatusProperty, value);
        }

        public static readonly DependencyProperty LegacyImportBusyProperty =
            DependencyProperty.Register(
                nameof(LegacyImportBusy),
                typeof(bool),
                typeof(ManualSettingsView),
                new PropertyMetadata(false));

        public bool LegacyImportBusy
        {
            get => (bool)GetValue(LegacyImportBusyProperty);
            set => SetValue(LegacyImportBusyProperty, value);
        }

        public new ManualSettings Settings => _manualSettings;

        public ManualSettingsView(IPlayniteAPI playniteApi, ILogger logger, PlayniteAchievementsSettings pluginSettings)
        {
            _playniteApi = playniteApi;
            _logger = logger;
            _pluginSettings = pluginSettings;
            InitializeComponent();
        }

        public override void Initialize(IProviderSettings settings)
        {
            _manualSettings = settings as ManualSettings;
            base.Initialize(settings);
            EnsureLegacyImportPathDefault();
            SetLegacyImportStatus(ResourceProvider.GetString("LOCPlayAch_Settings_Manual_Legacy_StatusIdle"));
        }

        private void EnsureLegacyImportPathDefault()
        {
            if (!string.IsNullOrWhiteSpace(LegacyImportPath))
            {
                return;
            }

            var extensionsDataPath = _playniteApi?.Paths?.ExtensionsDataPath;
            if (string.IsNullOrWhiteSpace(extensionsDataPath))
            {
                return;
            }

            LegacyImportPath = Path.Combine(extensionsDataPath, SuccessStoryExtensionId, "SuccessStory");
        }

        private void ManualLegacyBrowse_Click(object sender, RoutedEventArgs e)
        {
            EnsureLegacyImportPathDefault();

            var selectedPath = _playniteApi?.Dialogs?.SelectFolder();
            if (string.IsNullOrWhiteSpace(selectedPath))
            {
                return;
            }

            LegacyImportPath = selectedPath;
            SetLegacyImportStatus(ResourceProvider.GetString("LOCPlayAch_Settings_Manual_Legacy_StatusPathUpdated"));
        }

        private async void ManualLegacyImport_Click(object sender, RoutedEventArgs e)
        {
            if (LegacyImportBusy)
            {
                return;
            }

            EnsureLegacyImportPathDefault();
            var importPath = LegacyImportPath;

            if (string.IsNullOrWhiteSpace(importPath) || !Directory.Exists(importPath))
            {
                var invalidPathMessage = string.Format(
                    ResourceProvider.GetString("LOCPlayAch_Settings_Manual_Legacy_PathInvalid"),
                    importPath ?? string.Empty);

                _playniteApi?.Dialogs?.ShowMessage(
                    invalidPathMessage,
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                SetLegacyImportStatus(invalidPathMessage);
                return;
            }

            SetLegacyImportBusy(true);
            SetLegacyImportStatus(ResourceProvider.GetString("LOCPlayAch_Settings_Manual_Legacy_StatusRunning"));

            LegacyManualImportResult importResult;
            try
            {
                importResult = await Task.Run(() => ImportLegacyManualLinks(importPath)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Legacy manual import failed.");

                var failureMessage = string.Format(
                    L("LOCPlayAch_Settings_Manual_Legacy_ImportFailed", "Legacy import failed: {0}"),
                    ex.Message);

                SetLegacyImportStatus(failureMessage);
                SetLegacyImportBusy(false);
                return;
            }

            SetLegacyImportBusy(false);
            SetLegacyImportStatus(BuildLegacyManualImportSummary(importResult));
        }

        private LegacyManualImportResult ImportLegacyManualLinks(string folderPath)
        {
            var importer = new LegacyManualLinkImporter(
                () => _pluginSettings?.Persisted,
                gameId => _playniteApi?.Database?.Games?.Get(gameId) != null,
                gameId => false,
                _logger,
                gameCustomDataStore: PlayniteAchievementsPlugin.Instance?.GameCustomDataStore);

            var result = importer.Import(folderPath) ?? new LegacyManualImportResult();
            return result;
        }

        private string BuildLegacyManualImportSummary(LegacyManualImportResult result)
        {
            var lines = new List<string>
            {
                ResourceProvider.GetString("LOCPlayAch_Settings_Manual_Legacy_SummaryHeader"),
                string.Format(ResourceProvider.GetString("LOCPlayAch_Settings_Manual_Legacy_SummaryScanned"), result.Scanned),
                string.Format(ResourceProvider.GetString("LOCPlayAch_Settings_Manual_Legacy_SummaryImported"), result.Imported),
                string.Format(ResourceProvider.GetString("LOCPlayAch_Settings_Manual_Legacy_SummaryParseFailed"), result.ParseFailures),
                string.Format(ResourceProvider.GetString("LOCPlayAch_Settings_Manual_Legacy_SummarySkipNotManual"), result.SkippedNotManual),
                string.Format(ResourceProvider.GetString("LOCPlayAch_Settings_Manual_Legacy_SummarySkipIgnored"), result.SkippedIgnored),
                string.Format(ResourceProvider.GetString("LOCPlayAch_Settings_Manual_Legacy_SummarySkipInvalidFile"), result.SkippedInvalidFileName),
                string.Format(ResourceProvider.GetString("LOCPlayAch_Settings_Manual_Legacy_SummarySkipMissingGame"), result.SkippedGameMissing),
                string.Format(ResourceProvider.GetString("LOCPlayAch_Settings_Manual_Legacy_SummarySkipManualExists"), result.SkippedManualLinkExists),
                string.Format(ResourceProvider.GetString("LOCPlayAch_Settings_Manual_Legacy_SummarySkipCachedData"), result.SkippedCachedProviderData),
                string.Format(ResourceProvider.GetString("LOCPlayAch_Settings_Manual_Legacy_SummarySkipUnsupportedSource"), result.SkippedUnsupportedSource),
                string.Format(ResourceProvider.GetString("LOCPlayAch_Settings_Manual_Legacy_SummarySkipUnresolvedId"), result.SkippedUnresolvedSourceGameId)
            };

            if (result.UnsupportedSources.Count > 0)
            {
                lines.Add(ResourceProvider.GetString("LOCPlayAch_Settings_Manual_Legacy_UnsupportedSourcesHeader"));
                foreach (var pair in result.UnsupportedSources.OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase))
                {
                    lines.Add($"- {pair.Key}: {pair.Value}");
                }
            }

            return string.Join(Environment.NewLine, lines.Where(l => !string.IsNullOrWhiteSpace(l)));
        }

        private void SetLegacyImportStatus(string status)
        {
            if (Dispatcher.CheckAccess())
            {
                LegacyImportStatus = status;
            }
            else
            {
                Dispatcher.BeginInvoke(new Action(() => LegacyImportStatus = status));
            }
        }

        private void SetLegacyImportBusy(bool busy)
        {
            if (Dispatcher.CheckAccess())
            {
                LegacyImportBusy = busy;
            }
            else
            {
                Dispatcher.BeginInvoke(new Action(() => LegacyImportBusy = busy));
            }
        }

        private static string L(string key, string fallback)
        {
            var value = ResourceProvider.GetString(key);
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }
    }
}
