using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;
using PlayniteAchievements.Providers.Local;
using RelayCommand = PlayniteAchievements.Common.RelayCommand;

namespace PlayniteAchievements.ViewModels.ManageAchievements
{
    public sealed partial class ManageAchievementsViewModel
    {
        private GameOptionsCustomSchemaTab _selectedCustomSchemaTab = GameOptionsCustomSchemaTab.SchemaJson;
        private bool _hasLocalCustomSchemaOverride;
        private bool _localCustomSchemaEnabled;
        private string _localCustomSchemaOverrideValue = string.Empty;
        private string _localCustomSchemaOverrideInput = string.Empty;
        private string _customSchemaEditorText = string.Empty;
        private string _customSchemaEditorStatus = string.Empty;
        private bool _customSchemaEditorStatusIsError;
        private RelayCommand _applyLocalCustomSchemaOverrideCommand;
        private RelayCommand _clearLocalCustomSchemaOverrideCommand;
        private RelayCommand _browseLocalCustomSchemaOverrideCommand;
        private RelayCommand _reloadCustomSchemaEditorCommand;
        private RelayCommand _saveCustomSchemaEditorCommand;

        public GameOptionsCustomSchemaTab SelectedCustomSchemaTab
        {
            get => _selectedCustomSchemaTab;
            set => SetValue(ref _selectedCustomSchemaTab, value);
        }

        public bool HasLocalCustomSchemaOverride
        {
            get => _hasLocalCustomSchemaOverride;
            private set
            {
                if (SetValueAndReturn(ref _hasLocalCustomSchemaOverride, value))
                {
                    OnPropertyChanged(nameof(LocalCustomSchemaStatusText));
                    RaiseCustomSchemaCommandStates();
                }
            }
        }

        public bool LocalCustomSchemaEnabled
        {
            get => _localCustomSchemaEnabled;
            set
            {
                if (!HasGame || !SetValueAndReturn(ref _localCustomSchemaEnabled, value))
                {
                    return;
                }

                LocalSavesProvider.TrySetCustomSchemaEnabledOverride(_gameId, value, CurrentGameName, _persistSettingsForUi, _logger);
                if (value)
                {
                    _achievementOverridesService?.SetPreferredProviderOverride(_gameId, "Local");
                }

                _achievementOverridesService?.ClearGameData(_gameId, CurrentGameName);
                OnPropertyChanged(nameof(LocalCustomSchemaStatusText));
                TriggerRefresh();
            }
        }

        public string LocalCustomSchemaOverrideValue
        {
            get => _localCustomSchemaOverrideValue;
            private set
            {
                if (SetValueAndReturn(ref _localCustomSchemaOverrideValue, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(LocalCustomSchemaStatusText));
                }
            }
        }

        public string LocalCustomSchemaOverrideInput
        {
            get => _localCustomSchemaOverrideInput;
            set
            {
                if (SetValueAndReturn(ref _localCustomSchemaOverrideInput, value ?? string.Empty))
                {
                    RaiseCustomSchemaCommandStates();
                }
            }
        }

        public string LocalCustomSchemaStatusText
        {
            get
            {
                if (!HasLocalCustomSchemaOverride)
                {
                    return L("LOCPlayAch_GameOptions_Status_LocalCustomSchemaOverrideNone", "No custom schema override set");
                }

                return string.Format(
                    LocalCustomSchemaEnabled
                        ? L("LOCPlayAch_GameOptions_Status_LocalCustomSchemaOverrideEnabled", "Custom schema enabled: {0}")
                        : L("LOCPlayAch_GameOptions_Status_LocalCustomSchemaOverrideDisabled", "Custom schema disabled: {0}"),
                    LocalCustomSchemaOverrideValue);
            }
        }

        public string CustomSchemaEditorText
        {
            get => _customSchemaEditorText;
            set => SetValue(ref _customSchemaEditorText, value ?? string.Empty);
        }

        public string CustomSchemaEditorStatus
        {
            get => _customSchemaEditorStatus;
            private set => SetValue(ref _customSchemaEditorStatus, value ?? string.Empty);
        }

        public bool CustomSchemaEditorStatusIsError
        {
            get => _customSchemaEditorStatusIsError;
            private set => SetValue(ref _customSchemaEditorStatusIsError, value);
        }

        public RelayCommand ApplyLocalCustomSchemaOverrideCommand => _applyLocalCustomSchemaOverrideCommand ??= new RelayCommand(_ => ApplyLocalCustomSchemaOverride(), _ => HasGame);
        public RelayCommand ClearLocalCustomSchemaOverrideCommand => _clearLocalCustomSchemaOverrideCommand ??= new RelayCommand(_ => ClearLocalCustomSchemaOverride(), _ => HasGame && HasLocalCustomSchemaOverride);
        public RelayCommand BrowseLocalCustomSchemaOverrideCommand => _browseLocalCustomSchemaOverrideCommand ??= new RelayCommand(_ => BrowseLocalCustomSchemaOverride(), _ => HasGame);
        public RelayCommand ReloadCustomSchemaEditorCommand => _reloadCustomSchemaEditorCommand ??= new RelayCommand(_ => ReloadCustomSchemaEditor(), _ => HasGame);
        public RelayCommand SaveCustomSchemaEditorCommand => _saveCustomSchemaEditorCommand ??= new RelayCommand(_ => SaveCustomSchemaEditor(), _ => HasGame);

        private void RefreshCustomSchemaState()
        {
            if (LocalSavesProvider.TryGetCustomSchemaPathOverride(_gameId, out var schemaPath))
            {
                HasLocalCustomSchemaOverride = true;
                LocalCustomSchemaOverrideValue = schemaPath;
                LocalCustomSchemaOverrideInput = schemaPath;
            }
            else
            {
                HasLocalCustomSchemaOverride = false;
                LocalCustomSchemaOverrideValue = string.Empty;
                LocalCustomSchemaOverrideInput = string.Empty;
            }

            if (LocalSavesProvider.TryGetCustomSchemaEnabledOverride(_gameId, out var enabled))
            {
                SetValue(ref _localCustomSchemaEnabled, enabled);
            }
            else
            {
                SetValue(ref _localCustomSchemaEnabled, false);
            }

            OnPropertyChanged(nameof(LocalCustomSchemaEnabled));
            OnPropertyChanged(nameof(LocalCustomSchemaStatusText));
            ReloadCustomSchemaEditor();
        }

        private void RaiseCustomSchemaCommandStates()
        {
            _applyLocalCustomSchemaOverrideCommand?.RaiseCanExecuteChanged();
            _clearLocalCustomSchemaOverrideCommand?.RaiseCanExecuteChanged();
            _browseLocalCustomSchemaOverrideCommand?.RaiseCanExecuteChanged();
            _reloadCustomSchemaEditorCommand?.RaiseCanExecuteChanged();
            _saveCustomSchemaEditorCommand?.RaiseCanExecuteChanged();
        }

        private void ApplyLocalCustomSchemaOverride()
        {
            var text = (LocalCustomSchemaOverrideInput ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                ShowCustomSchemaWarning("LOCPlayAch_GameOptions_LocalCustomSchema_Invalid", "Please enter a valid custom schema JSON path.");
                return;
            }

            if (!File.Exists(text))
            {
                ShowCustomSchemaWarning("LOCPlayAch_GameOptions_LocalCustomSchema_NotFound", "The selected custom schema file does not exist.");
                return;
            }

            if (!string.Equals(Path.GetExtension(text), ".json", StringComparison.OrdinalIgnoreCase))
            {
                ShowCustomSchemaWarning("LOCPlayAch_GameOptions_LocalCustomSchema_InvalidExtension", "Custom schema file must be a .json file.");
                return;
            }

            if (TrySetLocalCustomSchemaOverride(text))
            {
                SetValue(ref _localCustomSchemaEnabled, true);
                Reload();
            }
        }

        private void ClearLocalCustomSchemaOverride()
        {
            if (TryClearLocalCustomSchemaOverride())
            {
                Reload();
            }
        }

        private void BrowseLocalCustomSchemaOverride()
        {
            var dialog = new OpenFileDialog
            {
                Filter = "JSON Files (*.json)|*.json|All Files (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };

            if (dialog.ShowDialog() == true)
            {
                LocalCustomSchemaOverrideInput = dialog.FileName;
            }
        }

        private void ReloadCustomSchemaEditor()
        {
            if (!HasGame || string.IsNullOrWhiteSpace(LocalCustomSchemaOverrideValue))
            {
                CustomSchemaEditorText = BuildDefaultCustomSchemaTemplate();
                CustomSchemaEditorStatus = L("LOCPlayAch_GameOptions_CustomSchemaEditor_NoFilePrompt", "No custom schema file selected. Enter a path above and click Apply, or click Save to create one.");
                CustomSchemaEditorStatusIsError = false;
                return;
            }

            if (!File.Exists(LocalCustomSchemaOverrideValue))
            {
                CustomSchemaEditorText = string.Empty;
                CustomSchemaEditorStatus = L("LOCPlayAch_GameOptions_CustomSchemaEditor_FileMissing", "Custom schema file was not found.");
                CustomSchemaEditorStatusIsError = true;
                return;
            }

            try
            {
                CustomSchemaEditorText = File.ReadAllText(LocalCustomSchemaOverrideValue, Encoding.UTF8);
                CustomSchemaEditorStatus = string.Format(L("LOCPlayAch_GameOptions_CustomSchemaEditor_Loaded", "Loaded custom schema: {0}"), LocalCustomSchemaOverrideValue);
                CustomSchemaEditorStatusIsError = false;
            }
            catch (Exception ex)
            {
                CustomSchemaEditorStatus = string.Format(L("LOCPlayAch_GameOptions_CustomSchemaEditor_LoadFailed", "Failed to load custom schema: {0}"), ex.Message);
                CustomSchemaEditorStatusIsError = true;
            }
        }

        private void SaveCustomSchemaEditor()
        {
            if (!HasGame)
            {
                return;
            }

            var targetPath = LocalCustomSchemaOverrideValue?.Trim();
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                var dialog = new SaveFileDialog
                {
                    Filter = "JSON Files (*.json)|*.json|All Files (*.*)|*.*",
                    AddExtension = true,
                    DefaultExt = ".json",
                    OverwritePrompt = true,
                    FileName = BuildDefaultCustomSchemaFileName()
                };

                if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.FileName))
                {
                    return;
                }

                targetPath = dialog.FileName.Trim();
                if (!string.Equals(Path.GetExtension(targetPath), ".json", StringComparison.OrdinalIgnoreCase))
                {
                    targetPath += ".json";
                }

                if (!TrySetLocalCustomSchemaOverride(targetPath))
                {
                    CustomSchemaEditorStatus = L("LOCPlayAch_GameOptions_CustomSchemaEditor_CreateFailed", "Failed to create a custom schema override path.");
                    CustomSchemaEditorStatusIsError = true;
                    return;
                }

                HasLocalCustomSchemaOverride = true;
                LocalCustomSchemaOverrideValue = targetPath;
                LocalCustomSchemaOverrideInput = targetPath;
                SetValue(ref _localCustomSchemaEnabled, true);
                OnPropertyChanged(nameof(LocalCustomSchemaEnabled));
            }

            var raw = CustomSchemaEditorText ?? string.Empty;
            if (string.IsNullOrWhiteSpace(raw))
            {
                ShowCustomSchemaWarning("LOCPlayAch_GameOptions_CustomSchemaEditor_Empty", "Custom schema content cannot be empty.");
                return;
            }

            try
            {
                var parsed = JToken.Parse(raw);
                var formatted = parsed.ToString(Newtonsoft.Json.Formatting.Indented);
                var directory = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(targetPath, formatted, Encoding.UTF8);
                CustomSchemaEditorText = formatted;
                CustomSchemaEditorStatus = L("LOCPlayAch_GameOptions_CustomSchemaEditor_SaveSuccess", "Custom schema saved successfully.");
                CustomSchemaEditorStatusIsError = false;
                _achievementOverridesService?.ClearGameData(_gameId, CurrentGameName);
                TriggerRefresh();
            }
            catch (Exception ex)
            {
                CustomSchemaEditorStatus = string.Format(L("LOCPlayAch_GameOptions_CustomSchemaEditor_SaveFailed", "Failed to save custom schema: {0}"), ex.Message);
                CustomSchemaEditorStatusIsError = true;
            }
        }

        private bool TrySetLocalCustomSchemaOverride(string schemaPath)
        {
            if (!HasGame || string.IsNullOrWhiteSpace(schemaPath))
            {
                return false;
            }

            if (!LocalSavesProvider.TrySetCustomSchemaPathOverride(_gameId, schemaPath, CurrentGameName, _persistSettingsForUi, _logger))
            {
                return false;
            }

            _achievementOverridesService?.SetPreferredProviderOverride(_gameId, "Local");
            _achievementOverridesService?.ClearGameData(_gameId, CurrentGameName);
            TriggerRefresh();
            return true;
        }

        private bool TryClearLocalCustomSchemaOverride()
        {
            if (!HasGame)
            {
                return false;
            }

            if (!LocalSavesProvider.TryClearCustomSchemaPathOverride(_gameId, CurrentGameName, _persistSettingsForUi, _logger))
            {
                return false;
            }

            LocalSavesProvider.TryClearCustomSchemaEnabledOverride(_gameId, CurrentGameName, _persistSettingsForUi, _logger);
            _achievementOverridesService?.ClearGameData(_gameId, CurrentGameName);
            TriggerRefresh();
            return true;
        }

        private string BuildDefaultCustomSchemaFileName()
        {
            var rawName = (GameName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(rawName))
            {
                rawName = _gameId.ToString();
            }

            var invalidChars = Path.GetInvalidFileNameChars();
            var sanitized = new string(rawName.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray()).Trim();
            return (string.IsNullOrWhiteSpace(sanitized) ? _gameId.ToString() : sanitized) + "_custom_schema.json";
        }

        private string BuildDefaultCustomSchemaTemplate()
        {
            var rows = new JArray
            {
                new JObject
                {
                    ["name"] = "achievement_api_name_1",
                    ["apiName"] = "achievement_api_name_1",
                    ["api_name"] = "achievement_api_name_1",
                    ["id"] = "achievement_api_name_1",
                    ["key"] = "achievement_api_name_1",
                    ["displayName"] = "Achievement Title 1",
                    ["display_name"] = "Achievement Title 1",
                    ["title"] = "Achievement Title 1",
                    ["description"] = "Achievement description 1",
                    ["desc"] = "Achievement description 1",
                    ["icon"] = string.Empty,
                    ["unlockedIcon"] = string.Empty,
                    ["image"] = string.Empty,
                    ["icon_gray"] = string.Empty,
                    ["icongray"] = string.Empty,
                    ["lockedIcon"] = string.Empty,
                    ["hidden"] = 0,
                    ["globalPercent"] = 0.0,
                    ["percent"] = 0.0
                },
                new JObject
                {
                    ["name"] = "achievement_api_name_2",
                    ["apiName"] = "achievement_api_name_2",
                    ["api_name"] = "achievement_api_name_2",
                    ["id"] = "achievement_api_name_2",
                    ["key"] = "achievement_api_name_2",
                    ["displayName"] = "Achievement Title 2",
                    ["display_name"] = "Achievement Title 2",
                    ["title"] = "Achievement Title 2",
                    ["description"] = "Achievement description 2",
                    ["desc"] = "Achievement description 2",
                    ["icon"] = string.Empty,
                    ["unlockedIcon"] = string.Empty,
                    ["image"] = string.Empty,
                    ["icon_gray"] = string.Empty,
                    ["icongray"] = string.Empty,
                    ["lockedIcon"] = string.Empty,
                    ["hidden"] = 1,
                    ["globalPercent"] = 12.34,
                    ["percent"] = 12.34
                }
            };

            var root = new JObject
            {
                ["achievements"] = rows
            };

            return root.ToString(Newtonsoft.Json.Formatting.Indented);
        }

        private void ShowCustomSchemaWarning(string resourceKey, string fallback)
        {
            _playniteApi?.Dialogs?.ShowMessage(
                L(resourceKey, fallback),
                L("LOCPlayAch_Title_PluginName", "Playnite Achievements"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }
}
