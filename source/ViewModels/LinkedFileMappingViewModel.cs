using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.LinkedFiles;
using PlayniteAchievements.ViewModels.Items;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ObservableObject = PlayniteAchievements.Common.ObservableObject;
using RelayCommand = PlayniteAchievements.Common.RelayCommand;

namespace PlayniteAchievements.ViewModels
{
    /// <summary>What the mapping window needs from the plugin, as plain delegates so it can be tested.</summary>
    public sealed class LinkedFileMappingServices
    {
        public Func<GameCustomDataFile> LoadRecord { get; set; }

        /// <summary>Writes one change to the game's record.</summary>
        public Action<Action<GameCustomDataFile>> UpdateRecord { get; set; }

        public Func<string, LinkedFileSchema, LinkedFileReadResult> ReadFile { get; set; } = LinkedFileTableReaders.Read;

        public Func<IReadOnlyList<AchievementDetail>> LoadAchievements { get; set; }

        public LinkedFileSchemaStore SchemaStore { get; set; }

        /// <summary>Reads the stored link and writes what the file says.</summary>
        public Func<LinkedFileApplyReport> ApplyNow { get; set; }

        /// <summary>Opens a file picker at the given path and returns the chosen file, or null.</summary>
        public Func<string, string> BrowseFile { get; set; }

        /// <summary>Asks a yes or no question.</summary>
        public Func<string, bool> Confirm { get; set; }

        public Action<string> ShowMessage { get; set; }

        /// <summary>Asks for a mapping name with the given default; null when cancelled.</summary>
        public Func<string, string> AskName { get; set; }

        /// <summary>Runs work off the UI thread and returns to it; tests run it inline.</summary>
        public Func<Func<LinkedFileReadResult>, Task<LinkedFileReadResult>> RunRead { get; set; } = work => Task.Run(work);

        public Func<string, string> Localize { get; set; } = key => ResourceProvider.GetString(key);
    }

    /// <summary>
    /// The Link File window: the file and its format, the saved mappings, a preview whose column
    /// headers pick the achievement field each column feeds, how rows match, and what the match
    /// would do. Nothing is stored on the game until Apply, which saves the link, reads the file
    /// and closes the window; Cancel leaves the game as it was, so a half-finished mapping never
    /// stays behind. Saved mappings are written to their own list when saved.
    /// </summary>
    public sealed class LinkedFileMappingViewModel : ObservableObject
    {
        public const int PreviewRowLimit = 50;

        private readonly LinkedFileMappingServices _services;
        private readonly Dictionary<LinkedFileField, LinkedFileOption<LinkedFileField>> _fieldOptions;
        private LinkedFileReadResult _lastRead;
        private int _readGeneration;
        private bool _isLoading;

        private string _filePath;
        private LinkedFileOption<LinkedFileFormat> _selectedFormat;
        private string _delimiterText;
        private bool _hasHeader = true;
        private string _rootPath;
        private string _sheetName;
        private string _rowElementPath;
        private bool _createUnmatched;
        private LinkedFileOption<LinkedFileTimeFormat> _selectedTimeFormat;
        private string _customTimeFormat;
        private bool _timesAreLocal = true;
        private string _unlockedValues;
        private string _mappingName;
        private string _selectedSavedName;
        private string _readMessage;
        private string _resultText;
        private string _problemText;
        private bool _isReading;
        private IReadOnlyList<LinkedFilePreviewRow> _previewRows = Array.Empty<LinkedFilePreviewRow>();
        private LinkedFileTable _previewTable = LinkedFileTable.Empty;
        private string _previewCountText;
        private string _unmatchedAchievementsHeader;
        private bool _isReloadingNames;

        public LinkedFileMappingViewModel(LinkedFileMappingServices services)
        {
            _services = services ?? throw new ArgumentNullException(nameof(services));

            FormatOptions = new[]
            {
                new LinkedFileOption<LinkedFileFormat>(LinkedFileFormat.Delimited, L("LOCPlayAch_LinkedFile_Format_Delimited")),
                new LinkedFileOption<LinkedFileFormat>(LinkedFileFormat.Json, "JSON"),
                new LinkedFileOption<LinkedFileFormat>(LinkedFileFormat.Toml, "TOML"),
                new LinkedFileOption<LinkedFileFormat>(LinkedFileFormat.Xlsx, L("LOCPlayAch_LinkedFile_Format_Xlsx")),
                new LinkedFileOption<LinkedFileFormat>(LinkedFileFormat.Xml, "XML"),
                new LinkedFileOption<LinkedFileFormat>(LinkedFileFormat.Ini, "INI"),
                new LinkedFileOption<LinkedFileFormat>(LinkedFileFormat.Binary, L("LOCPlayAch_LinkedFile_Format_Binary"))
            };

            TimeFormatOptions = new[]
            {
                new LinkedFileOption<LinkedFileTimeFormat>(LinkedFileTimeFormat.Auto, L("LOCPlayAch_Common_Automatic")),
                new LinkedFileOption<LinkedFileTimeFormat>(LinkedFileTimeFormat.UnixSeconds, L("LOCPlayAch_LinkedFile_TimeFormat_UnixSeconds")),
                new LinkedFileOption<LinkedFileTimeFormat>(LinkedFileTimeFormat.UnixMilliseconds, L("LOCPlayAch_LinkedFile_TimeFormat_UnixMilliseconds")),
                new LinkedFileOption<LinkedFileTimeFormat>(LinkedFileTimeFormat.WindowsFileTime, L("LOCPlayAch_LinkedFile_TimeFormat_WindowsFileTime")),
                new LinkedFileOption<LinkedFileTimeFormat>(LinkedFileTimeFormat.Iso8601, "ISO 8601"),
                new LinkedFileOption<LinkedFileTimeFormat>(LinkedFileTimeFormat.Custom, L("LOCPlayAch_Common_Custom"))
            };

            FieldOptions = new[]
            {
                new LinkedFileOption<LinkedFileField>(LinkedFileField.Ignore, L("LOCPlayAch_Column_Ignore")),
                new LinkedFileOption<LinkedFileField>(LinkedFileField.ApiName, L("LOCPlayAch_Column_ApiName")),
                new LinkedFileOption<LinkedFileField>(LinkedFileField.DisplayName, L("LOCPlayAch_Common_Name")),
                new LinkedFileOption<LinkedFileField>(LinkedFileField.Description, L("LOCPlayAch_Common_Description")),
                new LinkedFileOption<LinkedFileField>(LinkedFileField.Unlocked, L("LOCPlayAch_Common_Unlocked")),
                new LinkedFileOption<LinkedFileField>(LinkedFileField.UnlockTime, L("LOCPlayAch_Common_UnlockTime")),
                new LinkedFileOption<LinkedFileField>(LinkedFileField.ProgressNum, L("LOCPlayAch_Progress")),
                new LinkedFileOption<LinkedFileField>(LinkedFileField.ProgressDenom, L("LOCPlayAch_ManageAchievements_Custom_ProgressDenom")),
                new LinkedFileOption<LinkedFileField>(LinkedFileField.Hidden, L("LOCPlayAch_Filter_Hidden")),
                new LinkedFileOption<LinkedFileField>(LinkedFileField.UnlockedIconPath, L("LOCPlayAch_ManageAchievements_Custom_UnlockedIcon")),
                new LinkedFileOption<LinkedFileField>(LinkedFileField.LockedIconPath, L("LOCPlayAch_ManageAchievements_Custom_LockedIcon")),
                new LinkedFileOption<LinkedFileField>(LinkedFileField.Points, L("LOCPlayAch_Column_Points"))
            };
            _fieldOptions = FieldOptions.ToDictionary(option => option.Value);

            DelimiterOptions = new[] { L("LOCPlayAch_Common_Automatic"), ",", ";", "Tab", "Space", "|" };

            _selectedFormat = FormatOptions[0];
            _selectedTimeFormat = TimeFormatOptions[0];
            _delimiterText = DelimiterOptions[0];

            BrowseCommand = new RelayCommand(_ => Browse());
            SaveAsCommand = new RelayCommand(_ => SaveAs());
            RenameSavedCommand = new RelayCommand(_ => RenameSaved(), _ => !string.IsNullOrWhiteSpace(SelectedSavedName));
            DeleteSavedCommand = new RelayCommand(_ => DeleteSaved(), _ => !string.IsNullOrWhiteSpace(SelectedSavedName));
            ApplyNowCommand = new RelayCommand(_ => ApplyNow(), _ => CanApplyNow);
            UnlinkCommand = new RelayCommand(_ => Unlink(), _ => HasStoredLink);
            RereadCommand = new RelayCommand(_ => Reread());

            LoadFromRecord();
            ReloadSavedNames();
        }

        // ---------------------------------------------------------------- options

        public IReadOnlyList<LinkedFileOption<LinkedFileFormat>> FormatOptions { get; }

        public IReadOnlyList<LinkedFileOption<LinkedFileTimeFormat>> TimeFormatOptions { get; }

        public IReadOnlyList<LinkedFileOption<LinkedFileField>> FieldOptions { get; }

        public IReadOnlyList<string> DelimiterOptions { get; }

        public ObservableCollection<string> SavedNames { get; } = new ObservableCollection<string>();

        public ObservableCollection<LinkedFileColumnItem> Columns { get; } = new ObservableCollection<LinkedFileColumnItem>();

        public ObservableCollection<LinkedFileMatchItem> UnmatchedAchievements { get; } = new ObservableCollection<LinkedFileMatchItem>();

        public ObservableCollection<string> SheetNames { get; } = new ObservableCollection<string>();

        /// <summary>The lists and objects a JSON or TOML file offers as its rows.</summary>
        public ObservableCollection<string> RowPaths { get; } = new ObservableCollection<string>();

        /// <summary>Raised after the preview's columns were rebuilt, so the view regenerates its grid columns.</summary>
        public event EventHandler ColumnsRebuilt;

        // ---------------------------------------------------------------- commands

        public RelayCommand BrowseCommand { get; }

        public RelayCommand SaveAsCommand { get; }

        public RelayCommand RenameSavedCommand { get; }

        public RelayCommand DeleteSavedCommand { get; }

        public RelayCommand ApplyNowCommand { get; }

        public RelayCommand UnlinkCommand { get; }

        public RelayCommand RereadCommand { get; }

        /// <summary>True once the window stored, applied or removed a link, so the host reloads.</summary>
        public bool ChangedLink { get; private set; }

        /// <summary>Raised when the window should close, after an unlink.</summary>
        public event EventHandler RequestClose;

        // ---------------------------------------------------------------- file and format

        public string FilePath
        {
            get => _filePath;
            set
            {
                var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim().Trim('"');
                if (string.Equals(normalized, _filePath, StringComparison.Ordinal))
                {
                    return;
                }

                _filePath = normalized;
                OnPropertyChanged();
                OnPropertyChanged(nameof(FileName));
                OnPropertyChanged(nameof(IsFileMissing));
                if (!_isLoading)
                {
                    var detected = LinkedFileTableReaders.DetectFormat(_filePath);
                    if (detected.HasValue)
                    {
                        SetFormatSilently(detected.Value);
                    }

                    OnSettingChanged(reread: true);
                }
            }
        }

        public string FileName
        {
            get
            {
                try
                {
                    return string.IsNullOrWhiteSpace(_filePath) ? null : Path.GetFileName(_filePath);
                }
                catch (ArgumentException)
                {
                    return _filePath;
                }
            }
        }

        public bool IsFileMissing
        {
            get
            {
                try
                {
                    return !string.IsNullOrWhiteSpace(_filePath) && !File.Exists(_filePath);
                }
                catch (ArgumentException)
                {
                    return true;
                }
            }
        }

        public LinkedFileOption<LinkedFileFormat> SelectedFormat
        {
            get => _selectedFormat;
            set
            {
                if (value == null || ReferenceEquals(value, _selectedFormat))
                {
                    return;
                }

                _selectedFormat = value;
                RaiseFormatFlags();
                OnSettingChanged(reread: true);
            }
        }

        public bool IsDelimited => _selectedFormat?.Value == LinkedFileFormat.Delimited;

        public bool IsXlsx => _selectedFormat?.Value == LinkedFileFormat.Xlsx;

        public bool UsesRootPath => _selectedFormat?.Value == LinkedFileFormat.Json || _selectedFormat?.Value == LinkedFileFormat.Toml;

        public bool IsXml => _selectedFormat?.Value == LinkedFileFormat.Xml;

        public bool IsIni => _selectedFormat?.Value == LinkedFileFormat.Ini;

        public bool UsesHeaderRow => IsDelimited || IsXlsx;

        public string DelimiterText
        {
            get => _delimiterText;
            set => SetSetting(ref _delimiterText, value, reread: true);
        }

        public bool HasHeader
        {
            get => _hasHeader;
            set => SetSetting(ref _hasHeader, value, reread: true);
        }

        public string RootPath
        {
            get => _rootPath;
            set => SetSetting(ref _rootPath, value, reread: true);
        }

        public string SheetName
        {
            get => _sheetName;
            set => SetSetting(ref _sheetName, value, reread: true);
        }

        public string RowElementPath
        {
            get => _rowElementPath;
            set => SetSetting(ref _rowElementPath, value, reread: true);
        }

        // ---------------------------------------------------------------- matching and values

        public bool CreateUnmatched
        {
            get => _createUnmatched;
            set => SetSetting(ref _createUnmatched, value, reread: false);
        }

        /// <summary>Time options matter only while a column feeds the unlock time.</summary>
        public bool UsesTimeOptions => Columns.Any(column => column.Field == LinkedFileField.UnlockTime);

        /// <summary>Unlocked values matter only while a column feeds the unlocked state.</summary>
        public bool UsesUnlockedValues => Columns.Any(column => column.Field == LinkedFileField.Unlocked);

        public LinkedFileOption<LinkedFileTimeFormat> SelectedTimeFormat
        {
            get => _selectedTimeFormat;
            set
            {
                if (value == null || ReferenceEquals(value, _selectedTimeFormat))
                {
                    return;
                }

                _selectedTimeFormat = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsCustomTimeFormat));
                OnSettingChanged(reread: false);
            }
        }

        public bool IsCustomTimeFormat => _selectedTimeFormat?.Value == LinkedFileTimeFormat.Custom;

        public string CustomTimeFormat
        {
            get => _customTimeFormat;
            set => SetSetting(ref _customTimeFormat, value, reread: false);
        }

        public bool TimesAreLocal
        {
            get => _timesAreLocal;
            set => SetSetting(ref _timesAreLocal, value, reread: false);
        }

        public string UnlockedValues
        {
            get => _unlockedValues;
            set => SetSetting(ref _unlockedValues, value, reread: false);
        }

        // ---------------------------------------------------------------- saved mappings

        public string SelectedSavedName
        {
            get => _selectedSavedName;
            set
            {
                if (string.Equals(value, _selectedSavedName, StringComparison.Ordinal))
                {
                    return;
                }

                _selectedSavedName = value;
                OnPropertyChanged();
                RenameSavedCommand.RaiseCanExecuteChanged();
                DeleteSavedCommand.RaiseCanExecuteChanged();

                // Picking a preset loads it; refreshing the list only reselects.
                if (!_isReloadingNames && !string.IsNullOrWhiteSpace(value))
                {
                    ApplySaved();
                }
            }
        }

        // ---------------------------------------------------------------- preview and result

        public IReadOnlyList<LinkedFilePreviewRow> PreviewRows
        {
            get => _previewRows;
            private set => SetValue(ref _previewRows, value);
        }

        public string PreviewCountText
        {
            get => _previewCountText;
            private set => SetValue(ref _previewCountText, value);
        }

        public bool IsReading
        {
            get => _isReading;
            private set => SetValue(ref _isReading, value);
        }

        /// <summary>Why the file could not be read; null when it was.</summary>
        public string ReadMessage
        {
            get => _readMessage;
            private set
            {
                SetValue(ref _readMessage, value);
                OnPropertyChanged(nameof(HasReadMessage));
            }
        }

        public bool HasReadMessage => !string.IsNullOrWhiteSpace(_readMessage);

        /// <summary>The header of the list of achievements no row pairs with; null when there are none.</summary>
        public string UnmatchedAchievementsHeader
        {
            get => _unmatchedAchievementsHeader;
            private set
            {
                SetValue(ref _unmatchedAchievementsHeader, value);
                OnPropertyChanged(nameof(HasUnmatchedAchievements));
            }
        }

        public bool HasUnmatchedAchievements => !string.IsNullOrWhiteSpace(_unmatchedAchievementsHeader);

        /// <summary>What applying the file would do, as one line of the counts that are not zero.</summary>
        public string ResultText
        {
            get => _resultText;
            private set => SetValue(ref _resultText, value);
        }

        /// <summary>What the mapping still lacks; null when it is complete.</summary>
        public string ProblemText
        {
            get => _problemText;
            private set
            {
                SetValue(ref _problemText, value);
                OnPropertyChanged(nameof(HasProblem));
            }
        }

        public bool HasProblem => !string.IsNullOrWhiteSpace(_problemText);

        public bool HasStoredLink => _services.LoadRecord?.Invoke()?.LinkedFile != null;

        /// <summary>
        /// A complete mapping and a file that reads, or a file that does not exist yet: some
        /// programs write it only at the first unlock, so the link waits for it.
        /// </summary>
        public bool CanApplyNow =>
            !string.IsNullOrWhiteSpace(_filePath) &&
            (IsFileMissing || _lastRead?.Success == true) &&
            LinkedFileSchemaValidator.IsValid(BuildSchema());

        // ---------------------------------------------------------------- schema

        /// <summary>The mapping the window's settings describe.</summary>
        public LinkedFileSchema BuildSchema()
        {
            var schema = new LinkedFileSchema
            {
                Name = _mappingName,
                Format = _selectedFormat?.Value ?? LinkedFileFormat.Delimited,
                Delimiter = ParseDelimiter(_delimiterText),
                HasHeader = _hasHeader,
                SheetName = _sheetName,
                RootPath = _rootPath,
                RowElementPath = _rowElementPath,
                // Without a file there are no columns to show, so a loaded mapping stands as loaded.
                Columns = Columns.Count == 0 && _pendingMappings != null
                    ? _pendingMappings.Select(mapping => mapping.Clone()).ToList()
                    : Columns
                        .Where(column => column.IsMapped)
                        .Select(column => new LinkedFileColumnMapping { Column = column.ColumnName, Field = column.Field })
                        .ToList(),
                CreateUnmatchedRows = _createUnmatched,
                UnlockedTrueValues = (_unlockedValues ?? string.Empty)
                    .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(value => value.Trim())
                    .Where(value => value.Length > 0)
                    .ToList(),
                TimeFormat = _selectedTimeFormat?.Value ?? LinkedFileTimeFormat.Auto,
                CustomTimeFormat = _customTimeFormat,
                AssumeLocalTime = _timesAreLocal
            };

            return LinkedFileSchema.Normalize(schema);
        }

        /// <summary>Reads the file again with the current settings and refreshes the preview.</summary>
        public Task Reread()
        {
            var generation = ++_readGeneration;
            var path = _filePath;
            var schema = BuildSchema();
            if (string.IsNullOrWhiteSpace(path))
            {
                ApplyRead(generation, null);
                return Task.CompletedTask;
            }

            IsReading = true;
            var read = _services.RunRead(() => (_services.ReadFile ?? LinkedFileTableReaders.Read)(path, schema));
            return read.ContinueWith(
                task => ApplyRead(generation, task.Status == TaskStatus.RanToCompletion ? task.Result : null),
                System.Threading.CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                LinkedFileTaskSchedulers.FromCurrentSynchronizationContextOrDefault());
        }

        // ---------------------------------------------------------------- internals

        private void LoadFromRecord()
        {
            var link = _services.LoadRecord?.Invoke()?.LinkedFile;
            _isLoading = true;
            try
            {
                FilePath = link?.FilePath;
                ApplySchemaToSettings(link?.Schema);
            }
            finally
            {
                _isLoading = false;
            }

            _ = Reread();
        }

        private void ApplySchemaToSettings(LinkedFileSchema schema)
        {
            var wasLoading = _isLoading;
            _isLoading = true;
            try
            {
                _mappingName = schema?.Name;
                SetFormatSilently(schema?.Format ?? LinkedFileTableReaders.DetectFormat(_filePath) ?? LinkedFileFormat.Delimited);
                _delimiterText = FormatDelimiter(schema?.Delimiter);
                _hasHeader = schema?.HasHeader ?? true;
                _rootPath = schema?.RootPath;
                _sheetName = schema?.SheetName;
                _rowElementPath = schema?.RowElementPath;
                _createUnmatched = schema?.CreateUnmatchedRows ?? false;
                _selectedTimeFormat = TimeFormatOptions.FirstOrDefault(option => option.Value == (schema?.TimeFormat ?? LinkedFileTimeFormat.Auto)) ?? TimeFormatOptions[0];
                _customTimeFormat = schema?.CustomTimeFormat;
                _timesAreLocal = schema?.AssumeLocalTime ?? true;
                _unlockedValues = schema?.UnlockedTrueValues != null ? string.Join(", ", schema.UnlockedTrueValues) : null;
                _pendingMappings = schema?.Columns?.Select(mapping => mapping.Clone()).ToList();
            }
            finally
            {
                _isLoading = wasLoading;
            }

            foreach (var name in new[]
            {
                nameof(DelimiterText), nameof(HasHeader), nameof(RootPath), nameof(SheetName), nameof(RowElementPath),
                nameof(CreateUnmatched), nameof(SelectedTimeFormat),
                nameof(IsCustomTimeFormat), nameof(CustomTimeFormat), nameof(TimesAreLocal), nameof(UnlockedValues)
            })
            {
                OnPropertyChanged(name);
            }

            RaiseFormatFlags();
            ApplyPendingMappings();
        }

        /// <summary>Mappings from a loaded or saved schema, applied to the columns as they appear.</summary>
        private List<LinkedFileColumnMapping> _pendingMappings;

        private void ApplyPendingMappings()
        {
            if (_pendingMappings == null || Columns.Count == 0)
            {
                return;
            }

            var byColumn = _pendingMappings
                .Where(mapping => !string.IsNullOrWhiteSpace(mapping?.Column))
                .GroupBy(mapping => mapping.Column, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Field, StringComparer.OrdinalIgnoreCase);
            foreach (var column in Columns)
            {
                // By header name first, then by position for files without one.
                if (!byColumn.TryGetValue(column.ColumnName, out var field) &&
                    !byColumn.TryGetValue((column.Index + 1).ToString(CultureInfo.InvariantCulture), out field))
                {
                    field = LinkedFileField.Ignore;
                }

                column.SetFieldSilently(_fieldOptions[field]);
            }

            _pendingMappings = null;
            RaiseFieldFlags();
        }

        private void RaiseFieldFlags()
        {
            OnPropertyChanged(nameof(UsesTimeOptions));
            OnPropertyChanged(nameof(UsesUnlockedValues));
        }

        private void ApplyRead(int generation, LinkedFileReadResult read)
        {
            if (generation != _readGeneration)
            {
                return;
            }

            IsReading = false;
            _lastRead = read;

            SheetNames.Clear();
            foreach (var sheet in read?.SheetNames ?? Array.Empty<string>())
            {
                SheetNames.Add(sheet);
            }

            RowPaths.Clear();
            foreach (var path in read?.RowPaths ?? Array.Empty<string>())
            {
                RowPaths.Add(path);
            }

            if (read == null)
            {
                ReadMessage = string.IsNullOrWhiteSpace(_filePath) ? null : L("LOCPlayAch_LinkedFile_FileMissing");
            }
            else
            {
                ReadMessage = read.Success ? null : DescribeReadFailure(read);
            }

            RebuildColumns(read?.Success == true ? read.Table : LinkedFileTable.Empty);
            Recompute();
        }

        private void RebuildColumns(LinkedFileTable table)
        {
            // Keep what each column feeds across a re-read: the pending schema first, then the
            // columns already shown, matched by name and then by position.
            if (_pendingMappings == null && Columns.Count > 0)
            {
                _pendingMappings = Columns
                    .Where(column => column.IsMapped)
                    .Select(column => new LinkedFileColumnMapping { Column = column.ColumnName, Field = column.Field })
                    .ToList();
            }

            foreach (var column in Columns)
            {
                column.FieldChanged -= Column_FieldChanged;
            }

            Columns.Clear();
            for (var i = 0; i < table.Columns.Count; i++)
            {
                var column = new LinkedFileColumnItem(i, table.Columns[i], _fieldOptions[LinkedFileField.Ignore]);
                column.FieldChanged += Column_FieldChanged;
                Columns.Add(column);
            }

            if (_pendingMappings != null && table.Columns.Count > 0)
            {
                ApplyPendingMappings();
            }

            _previewTable = table;
            RaiseFieldFlags();
            PreviewRows = BuildPreviewRows(null, null);
            PreviewCountText = string.Format(
                L("LOCPlayAch_LinkedFile_PreviewRowsFormat"),
                PreviewRows.Count,
                table.Rows.Count);
            ColumnsRebuilt?.Invoke(this, EventArgs.Empty);
        }

        private void Column_FieldChanged(LinkedFileColumnItem changed)
        {
            // One column per field: picking a field another column feeds moves it here.
            if (changed.IsMapped)
            {
                foreach (var other in Columns.Where(column => !ReferenceEquals(column, changed) && column.Field == changed.Field))
                {
                    other.SetFieldSilently(_fieldOptions[LinkedFileField.Ignore]);
                }
            }

            RaiseFieldFlags();
            OnSettingChanged(reread: false);
        }

        private void OnSettingChanged(bool reread)
        {
            if (_isLoading)
            {
                return;
            }

            if (reread)
            {
                _ = Reread();
            }
            else
            {
                Recompute();
            }
        }

        /// <summary>Stores the window's link on the game. Only Apply calls it.</summary>
        private void Persist()
        {
            var schema = BuildSchema();
            if (string.IsNullOrWhiteSpace(_filePath) && (schema.Columns == null || schema.Columns.Count == 0))
            {
                return;
            }

            _services.UpdateRecord?.Invoke(record =>
            {
                var existing = record.LinkedFile;
                var samePath = string.Equals(existing?.FilePath, _filePath, StringComparison.OrdinalIgnoreCase);
                record.LinkedFile = new LinkedFileLink
                {
                    FilePath = _filePath,
                    Schema = schema,
                    // A new file has not been read yet.
                    LastReadUtc = samePath ? existing?.LastReadUtc : null,
                    LastReadStatus = samePath ? existing?.LastReadStatus : null
                };
            });

            ChangedLink = true;
            UnlinkCommand.RaiseCanExecuteChanged();
        }

        private void Recompute()
        {
            var schema = BuildSchema();
            var problems = LinkedFileSchemaValidator.Validate(schema);
            ProblemText = problems.Count == 0 ? null : string.Join(" ", problems.Select(DescribeProblem));

            UnmatchedAchievements.Clear();
            if (_lastRead?.Success != true)
            {
                ResultText = null;
                UnmatchedAchievementsHeader = null;
                ApplyNowCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(CanApplyNow));
                return;
            }

            var table = _lastRead.Table;
            var achievements = _services.LoadAchievements?.Invoke() ?? Array.Empty<AchievementDetail>();
            var record = _services.LoadRecord?.Invoke();
            var match = LinkedFileMatcher.Match(table, schema, achievements);
            var plan = LinkedFileApplier.Build(
                table,
                schema,
                SafeDirectory(_filePath),
                achievements,
                record?.ManualLink,
                record?.CustomAchievements,
                DateTime.UtcNow);
            var report = plan.Report;

            PreviewRows = BuildPreviewRows(match, plan);

            foreach (var achievement in match.UnmatchedAchievements.Take(PreviewRowLimit))
            {
                UnmatchedAchievements.Add(new LinkedFileMatchItem(
                    string.IsNullOrWhiteSpace(achievement.DisplayName) ? achievement.ApiName : achievement.DisplayName));
            }

            UnmatchedAchievementsHeader = match.UnmatchedAchievements.Count == 0
                ? null
                : string.Format(L("LOCPlayAch_LinkedFile_Count_UnmatchedAchievements"), match.UnmatchedAchievements.Count);
            ResultText = Summarize(report, match.UnmatchedRows.Count);

            ApplyNowCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(CanApplyNow));
        }

        /// <summary>The preview rows, each with the achievement it pairs with once a match ran.</summary>
        private IReadOnlyList<LinkedFilePreviewRow> BuildPreviewRows(LinkedFileMatchResult match, LinkedFileApplyPlan plan)
        {
            var matched = match?.Matches.ToDictionary(row => row.RowIndex, row => row.Achievement) ??
                          new Dictionary<int, AchievementDetail>();
            var rows = new List<LinkedFilePreviewRow>();
            var count = Math.Min(PreviewRowLimit, _previewTable.Rows.Count);
            for (var i = 0; i < count; i++)
            {
                var cells = _previewTable.Rows[i].ToArray();
                if (matched.TryGetValue(i, out var achievement))
                {
                    var name = string.IsNullOrWhiteSpace(achievement.DisplayName) ? achievement.ApiName : achievement.DisplayName;
                    rows.Add(new LinkedFilePreviewRow(cells, name, isMatched: true));
                }
                else if (plan?.CreatedRows.Contains(i) == true)
                {
                    rows.Add(new LinkedFilePreviewRow(cells, L("LOCPlayAch_LinkedFile_NewRow"), isMatched: true));
                }
                else
                {
                    rows.Add(new LinkedFilePreviewRow(cells, match == null ? null : L("LOCPlayAch_LinkedFile_NoMatch"), isMatched: false));
                }
            }

            return rows;
        }

        /// <summary>The matched count, then each other count that is not zero.</summary>
        private string Summarize(LinkedFileApplyReport report, int unmatchedRows)
        {
            var parts = new List<string> { string.Format(L("LOCPlayAch_LinkedFile_Count_Matched"), report.MatchedRows) };

            void Add(string key, int count)
            {
                if (count > 0)
                {
                    parts.Add(string.Format(L(key), count));
                }
            }

            Add("LOCPlayAch_LinkedFile_Count_Unlocked", report.Unlocked);
            Add("LOCPlayAch_LinkedFile_Count_Relocked", report.Relocked);
            Add("LOCPlayAch_LinkedFile_Count_Created", report.Created);
            Add("LOCPlayAch_LinkedFile_Count_UnmatchedRows", Math.Max(0, unmatchedRows - report.Created));
            Add("LOCPlayAch_LinkedFile_Count_Errors", report.ParseErrors);
            return string.Join(" \u00B7 ", parts);
        }

        private void Browse()
        {
            var chosen = _services.BrowseFile?.Invoke(_filePath);
            if (!string.IsNullOrWhiteSpace(chosen))
            {
                FilePath = chosen;
            }
        }

        private void ApplyNow()
        {
            Persist();
            var report = _services.ApplyNow?.Invoke();
            ChangedLink = true;
            if (report == null)
            {
                return;
            }

            var lines = new List<string>
            {
                report.Status == LinkedFileReadStatus.Ok
                    ? L("LOCPlayAch_LinkedFile_ApplyReportTitle")
                    : DescribeStatus(report.Status),
                Summarize(report, report.UnmatchedRows)
            };
            _services.ShowMessage?.Invoke(string.Join(Environment.NewLine, lines));
            RequestClose?.Invoke(this, EventArgs.Empty);
        }

        private void Unlink()
        {
            if (_services.Confirm?.Invoke(L("LOCPlayAch_LinkedFile_UnlinkConfirm")) != true)
            {
                return;
            }

            _services.UpdateRecord?.Invoke(record => record.LinkedFile = null);
            ChangedLink = true;
            RequestClose?.Invoke(this, EventArgs.Empty);
        }

        private void ReloadSavedNames()
        {
            var keep = _selectedSavedName;
            _isReloadingNames = true;
            try
            {
                SavedNames.Clear();
                foreach (var schema in _services.SchemaStore?.GetAll() ?? Array.Empty<LinkedFileSchema>())
                {
                    SavedNames.Add(schema.Name);
                }

                SelectedSavedName = SavedNames.FirstOrDefault(name => string.Equals(name, keep, StringComparison.OrdinalIgnoreCase))
                                    ?? SavedNames.FirstOrDefault(name => string.Equals(name, _mappingName, StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                _isReloadingNames = false;
            }
        }

        private void ApplySaved()
        {
            if (_services.SchemaStore == null || !_services.SchemaStore.TryGet(SelectedSavedName, out var schema))
            {
                return;
            }

            ApplySchemaToSettings(schema);
            _ = Reread();
        }

        private void SaveAs()
        {
            var store = _services.SchemaStore;
            if (store == null)
            {
                return;
            }

            var defaultName = _mappingName ?? Path.GetFileNameWithoutExtension(FileName ?? string.Empty);
            var name = LinkedFileSchema.SanitizeName(_services.AskName?.Invoke(defaultName));
            if (name.Length == 0)
            {
                return;
            }

            if (store.Contains(name) &&
                _services.Confirm?.Invoke(string.Format(L("LOCPlayAch_Presets_OverwriteConfirm"), name)) != true)
            {
                return;
            }

            _mappingName = name;
            store.Save(BuildSchema());
            _selectedSavedName = name;
            ReloadSavedNames();
        }

        private void RenameSaved()
        {
            var store = _services.SchemaStore;
            var oldName = SelectedSavedName;
            if (store == null || string.IsNullOrWhiteSpace(oldName))
            {
                return;
            }

            var newName = LinkedFileSchema.SanitizeName(_services.AskName?.Invoke(oldName));
            if (newName.Length == 0 || !store.Rename(oldName, newName))
            {
                return;
            }

            if (string.Equals(_mappingName, oldName, StringComparison.OrdinalIgnoreCase))
            {
                _mappingName = newName;
            }

            _selectedSavedName = newName;
            ReloadSavedNames();
        }

        private void DeleteSaved()
        {
            var store = _services.SchemaStore;
            var name = SelectedSavedName;
            if (store == null || string.IsNullOrWhiteSpace(name) ||
                _services.Confirm?.Invoke(string.Format(L("LOCPlayAch_Presets_DeleteConfirm"), name)) != true)
            {
                return;
            }

            store.Delete(name);
            _selectedSavedName = null;
            ReloadSavedNames();
        }

        private void SetSetting<T>(ref T field, T value, bool reread, [System.Runtime.CompilerServices.CallerMemberName] string propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return;
            }

            field = value;
            OnPropertyChanged(propertyName);
            OnSettingChanged(reread);
        }

        private void SetFormatSilently(LinkedFileFormat format)
        {
            var option = FormatOptions.FirstOrDefault(candidate => candidate.Value == format);
            if (option != null && !ReferenceEquals(option, _selectedFormat))
            {
                _selectedFormat = option;
                RaiseFormatFlags();
            }
        }

        private void RaiseFormatFlags()
        {
            OnPropertyChanged(nameof(SelectedFormat));
            OnPropertyChanged(nameof(IsDelimited));
            OnPropertyChanged(nameof(IsXlsx));
            OnPropertyChanged(nameof(UsesRootPath));
            OnPropertyChanged(nameof(IsXml));
            OnPropertyChanged(nameof(IsIni));
            OnPropertyChanged(nameof(UsesHeaderRow));
        }

        private string ParseDelimiter(string text)
        {
            if (!string.IsNullOrEmpty(text) && text.Trim().Length == 0)
            {
                return " ";
            }

            var trimmed = (text ?? string.Empty).Trim();
            if (trimmed.Length == 0 || string.Equals(trimmed, DelimiterOptions[0], StringComparison.CurrentCultureIgnoreCase))
            {
                return null;
            }

            return trimmed;
        }

        private string FormatDelimiter(string delimiter)
        {
            if (string.IsNullOrEmpty(delimiter))
            {
                return DelimiterOptions[0];
            }

            if (delimiter == "\t")
            {
                return "Tab";
            }

            return delimiter == " " ? "Space" : delimiter;
        }

        private string DescribeReadFailure(LinkedFileReadResult read)
        {
            var status = DescribeStatus(read.Status);
            return string.IsNullOrWhiteSpace(read.Message) || read.Status == LinkedFileReadStatus.FileMissing
                ? status
                : status + " " + read.Message;
        }

        private string DescribeStatus(LinkedFileReadStatus status)
        {
            switch (status)
            {
                case LinkedFileReadStatus.FileMissing:
                case LinkedFileReadStatus.NoFilePath:
                    return L("LOCPlayAch_LinkedFile_FileMissing");
                case LinkedFileReadStatus.FileUnstable:
                    return L("LOCPlayAch_LinkedFile_Status_Unstable");
                case LinkedFileReadStatus.SchemaInvalid:
                    return L("LOCPlayAch_LinkedFile_Status_MappingIncomplete");
                case LinkedFileReadStatus.NoAchievements:
                    return L("LOCPlayAch_LinkedFile_Status_NoAchievements");
                default:
                    return L("LOCPlayAch_LinkedFile_Status_ReadFailed");
            }
        }

        private string DescribeProblem(LinkedFileSchemaProblem problem)
        {
            switch (problem)
            {
                case LinkedFileSchemaProblem.NoColumns:
                    return L("LOCPlayAch_LinkedFile_Problem_NoColumns");
                case LinkedFileSchemaProblem.CreateUnmatchedNeedsNameOrApiName:
                    return L("LOCPlayAch_LinkedFile_Problem_CreateNeedsName");
                default:
                    return L("LOCPlayAch_LinkedFile_Problem_CustomTimeFormat");
            }
        }

        private static string SafeDirectory(string path)
        {
            try
            {
                return string.IsNullOrWhiteSpace(path) ? null : Path.GetDirectoryName(path);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        private string L(string key)
        {
            var value = _services.Localize?.Invoke(key);
            return string.IsNullOrEmpty(value) ? key : value;
        }
    }

    internal static class LinkedFileTaskSchedulers
    {
        /// <summary>The UI context when there is one, so continuations land on the dispatcher; the default scheduler in tests.</summary>
        public static TaskScheduler FromCurrentSynchronizationContextOrDefault()
        {
            return System.Threading.SynchronizationContext.Current != null
                ? TaskScheduler.FromCurrentSynchronizationContext()
                : TaskScheduler.Default;
        }
    }
}
