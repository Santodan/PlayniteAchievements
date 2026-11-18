using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.LinkedFiles;
using PlayniteAchievements.ViewModels.Items;
using PlayniteAchievements.ViewModels.ManageAchievements;
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

        /// <summary>What locked and hidden achievements show; everything when unset.</summary>
        public SpoilerSettings Spoilers { get; set; } = SpoilerSettings.ShowAll;
    }

    /// <summary>
    /// The Link File window: the file and its format, the saved mappings, how rows are matched to
    /// achievements, the game's achievements with the row each pairs with, and a preview of the
    /// file whose column headers pick the field each column feeds. Nothing is stored on the game
    /// until Apply, which saves the link, reads the file and closes the window; Cancel leaves the
    /// game as it was, so a half-finished mapping never stays behind. Saved mappings are written
    /// to their own list when saved.
    /// </summary>
    public sealed partial class LinkedFileMappingViewModel : ObservableObject
    {
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

        /// <summary>The file whose achievement list was last defaulted or loaded, so it is defaulted once.</summary>
        private string _rootPathSettledFor;
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
        private LinkedFileTable _previewRowsTable;
        private bool _isReloadingNames;
        private LinkedFileMatchResult _lastMatch;

        // Hand pairings by row key, staged until Apply.
        private readonly Dictionary<string, string> _pairings = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly LinkedFilePairingContext _pairing;

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
                new LinkedFileOption<LinkedFileField>(LinkedFileField.Points, L("LOCPlayAch_Column_Points")),
                new LinkedFileOption<LinkedFileField>(LinkedFileField.TrophyType, L("LOCPlayAch_Column_Trophy")),
                new LinkedFileOption<LinkedFileField>(LinkedFileField.Rarity, L("LOCPlayAch_Column_Rarity")),
                new LinkedFileOption<LinkedFileField>(LinkedFileField.Category, L("LOCPlayAch_Common_Category"))
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
            ToggleRevealCommand = new RelayCommand(_ => ToggleReveal());
            var automatic = MatchOption.Special(null, L("LOCPlayAch_Common_Automatic"));
            var none = MatchOption.Special(string.Empty, L("LOCPlayAch_Common_None"));
            _pairing = new LinkedFilePairingContext(automatic, none, PairRow);
            _rowPick = new LinkedFileRowPickContext(automatic, none, PairAchievement);

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

        /// <summary>Shows or masks the hidden and locked achievements' names, descriptions and icons.</summary>
        public RelayCommand ToggleRevealCommand { get; }

        private bool _isRevealed;
        private bool _hasMaskedAchievements;

        /// <summary>Whether the window shows masked achievements as they are.</summary>
        public bool IsRevealed
        {
            get => _isRevealed;
            private set => SetValue(ref _isRevealed, value);
        }

        /// <summary>Whether the display settings mask any of the game's achievements, so revealing does something.</summary>
        public bool HasMaskedAchievements
        {
            get => _hasMaskedAchievements;
            private set => SetValue(ref _hasMaskedAchievements, value);
        }

        /// <summary>Everything while the header toggle reveals; the display settings otherwise.</summary>
        private SpoilerSettings SpoilersFor(AchievementDetail achievement) =>
            _isRevealed ? SpoilerSettings.ShowAll : _services.Spoilers;

        private void ToggleReveal()
        {
            IsRevealed = !IsRevealed;
            Recompute();
        }

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
                MatchColumn = _matchColumn,
                MatchBy = _matchBy,
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

            // The window's own mapping has no match column until one is chosen, which is not an
            // older mapping to upgrade.
            return LinkedFileSchema.Normalize(schema, upgrade: false);
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
                _rootPathSettledFor = link?.FilePath;
                ApplySchemaToSettings(link?.Schema);
                _pairings.Clear();
                foreach (var pair in link?.Pairings ?? new Dictionary<string, string>())
                {
                    _pairings[pair.Key] = pair.Value ?? string.Empty;
                }
            }
            finally
            {
                _isLoading = false;
            }

            _ = Reread();
        }

        private bool HasAchievements()
        {
            return _services.LoadAchievements?.Invoke()?.Any(achievement => achievement != null) == true;
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
                // A new link on a game with no achievements adds the file's rows as new ones.
                _createUnmatched = schema?.CreateUnmatchedRows ?? !HasAchievements();
                _selectedTimeFormat = TimeFormatOptions.FirstOrDefault(option => option.Value == (schema?.TimeFormat ?? LinkedFileTimeFormat.Auto)) ?? TimeFormatOptions[0];
                _customTimeFormat = schema?.CustomTimeFormat;
                _timesAreLocal = schema?.AssumeLocalTime ?? true;
                _matchColumn = schema?.MatchColumn;
                _matchBy = schema?.MatchBy ?? LinkedFileMatchBy.ApiName;
                // A loaded mapping keeps its match; a new one takes the best the file offers.
                _matchChosen = _matchColumn != null;
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

            if (TryDefaultRootPath(read))
            {
                return;
            }

            if (read == null)
            {
                ReadMessage = string.IsNullOrWhiteSpace(_filePath) ? null : L("LOCPlayAch_LinkedFile_FileMissing");
            }
            else
            {
                ReadMessage = read.Success ? null : DescribeReadFailure(read);
            }

            var table = read?.Success == true ? read.Table : LinkedFileTable.Empty;
            RebuildColumns(table);
            FindMatches(table);
            Recompute();
        }

        /// <summary>
        /// On the first read of a newly chosen JSON or TOML file with no achievement list set,
        /// points the list at the file's only top-level list or object, such as <c>unlocked</c>
        /// in a file of <c>[unlocked.N]</c> sections. The whole file as one row is never what
        /// such a file means. Re-reads and returns true when it changed the list.
        /// </summary>
        private bool TryDefaultRootPath(LinkedFileReadResult read)
        {
            if (read?.Success != true || !UsesRootPath || !string.IsNullOrWhiteSpace(_rootPath) ||
                string.IsNullOrWhiteSpace(_filePath) ||
                string.Equals(_rootPathSettledFor, _filePath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            _rootPathSettledFor = _filePath;
            var top = read.RowPaths.Where(path => path.IndexOf('.') < 0 && path.IndexOf('[') < 0).ToList();
            if (top.Count != 1)
            {
                return false;
            }

            // The columns guessed for the whole-file row do not exist in the list's rows.
            _pendingMappings = null;
            foreach (var column in Columns)
            {
                column.FieldChanged -= Column_FieldChanged;
            }

            Columns.Clear();
            RootPath = top[0];
            return true;
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

            // Nothing mapped yet, as for a newly chosen file: start from a guess the user edits.
            if ((_pendingMappings == null || _pendingMappings.Count == 0) && table.Columns.Count > 0)
            {
                _pendingMappings = LinkedFileColumnGuesser.Guess(table);
            }

            foreach (var column in Columns)
            {
                column.FieldChanged -= Column_FieldChanged;
            }

            Columns.Clear();
            for (var i = 0; i < table.Columns.Count; i++)
            {
                var name = table.Columns[i];
                var column = new LinkedFileColumnItem(
                    i,
                    name,
                    _fieldOptions[LinkedFileField.Ignore],
                    string.Equals(name, LinkedFileSchema.PositionColumn, StringComparison.Ordinal) ? "#" : null);
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
            FollowMappedColumns();
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
                    LastReadStatus = samePath ? existing?.LastReadStatus : null,
                    Pairings = PairingsToStore(schema)
                };
            });

            ChangedLink = true;
            UnlinkCommand.RaiseCanExecuteChanged();
        }

        private void Recompute()
        {
            var schema = BuildSchema();
            var problems = LinkedFileSchemaValidator.Validate(schema)
                // Without achievements the match follows the API Name or Name column, and the
                // problem that names those columns says what is missing.
                .Where(problem => ShowsMatching || problem != LinkedFileSchemaProblem.NoMatchColumn)
                .ToList();
            ProblemText = problems.Count == 0 ? null : string.Join(" ", problems.Select(DescribeProblem));

            if (_lastRead?.Success != true)
            {
                ResultText = null;
                MatchSummary = null;
                _lastMatch = null;
                AchievementRows = Array.Empty<LinkedFileAchievementRow>();
                ApplyNowCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(CanApplyNow));
                return;
            }

            var table = _lastRead.Table;
            var achievements = _services.LoadAchievements?.Invoke() ?? Array.Empty<AchievementDetail>();
            var record = _services.LoadRecord?.Invoke();
            var match = LinkedFileMatcher.Match(table, schema, achievements, _pairings);
            _lastMatch = match;
            var plan = LinkedFileApplier.Build(
                table,
                schema,
                SafeDirectory(_filePath),
                achievements,
                record?.ManualLink,
                record?.CustomAchievements,
                DateTime.UtcNow,
                pairings: _pairings);
            var report = plan.Report;

            HasMaskedAchievements = achievements.Any(achievement =>
                achievement != null && AchievementSpoilerMask.Apply(achievement, _services.Spoilers, L).IsMasked);
            _pairing.Achievements = BuildAchievementOptions(achievements, match, table, schema);
            PreviewRows = BuildPreviewRows(match, plan, schema);

            // The match line counts the achievements, so the result line leaves the unlinked out.
            ResultText = Summarize(report, match.UnmatchedRows.Count, unmatchedAchievements: 0);
            MatchSummary = DescribeMatched(match, achievements);
            BuildAchievementRows(achievements, match, table, schema, record);

            ApplyNowCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(CanApplyNow));
        }

        /// <summary>
        /// The preview rows, each with the achievement it pairs with once a match ran. The rows
        /// of the same table are updated in place, so a pick keeps the grid where it was.
        /// </summary>
        private IReadOnlyList<LinkedFilePreviewRow> BuildPreviewRows(LinkedFileMatchResult match, LinkedFileApplyPlan plan, LinkedFileSchema schema = null)
        {
            var matched = match?.Matches.ToDictionary(row => row.RowIndex, row => row.Achievement) ??
                          new Dictionary<int, AchievementDetail>();
            var skipped = new HashSet<int>(match?.SkippedRows ?? new List<int>());
            var count = _previewTable.Rows.Count;
            var reuse = ReferenceEquals(_previewRowsTable, _previewTable) && _previewRows.Count == count;
            var rows = new List<LinkedFilePreviewRow>(count);
            var now = DateTime.UtcNow;
            for (var i = 0; i < count; i++)
            {
                string text;
                bool isMatched;
                AchievementSpoilerMask mask = null;
                if (matched.TryGetValue(i, out var achievement))
                {
                    mask = AchievementSpoilerMask.Apply(achievement, SpoilersFor(achievement), L);
                    text = mask.Name;
                    isMatched = true;
                }
                else if (plan?.CreatedRows.Contains(i) == true)
                {
                    text = L("LOCPlayAch_LinkedFile_NewRow");
                    isMatched = true;
                }
                else
                {
                    text = match == null ? null : skipped.Contains(i) ? L("LOCPlayAch_Common_None") : L("LOCPlayAch_LinkedFile_NoMatch");
                    isMatched = false;
                }

                var row = reuse ? _previewRows[i] : new LinkedFilePreviewRow(_previewTable.Rows[i].ToArray(), null, isMatched: false);
                var rowKey = match == null ? null : LinkedFileMatcher.RowKey(_previewTable, schema, i);
                row.SetMatch(
                    text,
                    isMatched,
                    rowKey != null && _pairings.ContainsKey(rowKey),
                    rowKey,
                    match == null ? null : _pairing);
                row.SetMatchIcon(mask?.Icon);
                row.SetCellErrors(match == null || schema == null ? null : DescribeCellErrors(schema, i, now));
                rows.Add(row);
            }

            _previewRowsTable = _previewTable;
            return reuse ? _previewRows : rows;
        }

        /// <summary>The game's achievements for the pairing dropdowns, each naming the row it pairs with.</summary>
        /// <summary>
        /// Per column index, why a row's cell cannot be read for the field it feeds, or null for a
        /// readable cell; null when the whole row reads.
        /// </summary>
        private string[] DescribeCellErrors(LinkedFileSchema schema, int rowIndex, DateTime nowUtc)
        {
            var values = LinkedFileRowValues.Parse(_previewTable, rowIndex, schema, SafeDirectory(_filePath), nowUtc);
            if (values.FailedFields.Count == 0)
            {
                return null;
            }

            var errors = new string[_previewTable.Columns.Count];
            foreach (var field in values.FailedFields)
            {
                if (!schema.TryGetColumn(field, out var column))
                {
                    continue;
                }

                var index = _previewTable.IndexOfColumn(column);
                if (index >= 0 && index < errors.Length)
                {
                    var name = FieldOptions.FirstOrDefault(option => option.Value == field)?.Name ?? field.ToString();
                    errors[index] = string.Format(L("LOCPlayAch_LinkedFile_CellUnreadable"), name);
                }
            }

            return errors;
        }

        /// <summary>
        /// An achievement as the window shows it: masked per the hidden and locked display settings,
        /// and found by its API name when its name is masked, so a search never confirms a hidden name.
        /// </summary>
        private MatchOption MaskedOption(AchievementDetail achievement)
        {
            var mask = AchievementSpoilerMask.Apply(achievement, SpoilersFor(achievement), L);
            return MatchOption.Create(achievement.ApiName, mask.Name, mask.Description, mask.Icon, mask.IsTitleMasked ? achievement.ApiName : null);
        }

        private IReadOnlyList<MatchOption> BuildAchievementOptions(
            IReadOnlyList<AchievementDetail> achievements,
            LinkedFileMatchResult match,
            LinkedFileTable table,
            LinkedFileSchema schema)
        {
            var rowByAchievement = new Dictionary<AchievementDetail, int>();
            foreach (var row in match.Matches)
            {
                rowByAchievement[row.Achievement] = row.RowIndex;
            }

            var options = new List<MatchOption>();
            foreach (var achievement in achievements.Where(achievement => achievement != null && !string.IsNullOrWhiteSpace(achievement.ApiName)))
            {
                var option = MaskedOption(achievement);
                if (rowByAchievement.TryGetValue(achievement, out var rowIndex))
                {
                    option.SetPairedWith(DescribeRow(table, schema, rowIndex), null);
                }

                options.Add(option);
            }

            return options;
        }

        /// <summary>The row's API name or name cell, else its number.</summary>
        private static string DescribeRow(LinkedFileTable table, LinkedFileSchema schema, int rowIndex)
        {
            var key = LinkedFileMatcher.RowKey(table, schema, rowIndex);
            return key.StartsWith("row:", StringComparison.Ordinal)
                ? "#" + (rowIndex + 1).ToString(CultureInfo.InvariantCulture)
                : key.Substring(key.IndexOf(':') + 1);
        }

        /// <summary>
        /// Stages a hand pairing: an API name, "" for none, or null to leave the row to matching.
        /// A pairing that a different way of matching makes on its own switches to that way instead.
        /// </summary>
        private void PairRow(string rowKey, string achievementKey)
        {
            if (string.IsNullOrEmpty(rowKey))
            {
                return;
            }

            if (!string.IsNullOrEmpty(achievementKey) && TryLearnMatch(rowKey, achievementKey))
            {
                return;
            }

            if (achievementKey == null)
            {
                _pairings.Remove(rowKey);
            }
            else
            {
                _pairings[rowKey] = achievementKey;
            }

            Recompute();
        }

        /// <summary>
        /// The pairings to store with the link, without those whose row the file no longer has;
        /// null when none are left.
        /// </summary>
        private Dictionary<string, string> PairingsToStore(LinkedFileSchema schema)
        {
            if (_pairings.Count == 0)
            {
                return null;
            }

            var kept = new Dictionary<string, string>(_pairings, StringComparer.Ordinal);
            var table = _lastRead?.Success == true ? _lastRead.Table : null;
            if (table != null)
            {
                var present = new HashSet<string>(StringComparer.Ordinal);
                for (var row = 0; row < table.Rows.Count; row++)
                {
                    present.Add(LinkedFileMatcher.RowKey(table, schema, row));
                }

                foreach (var key in kept.Keys.Where(key => !present.Contains(key)).ToList())
                {
                    kept.Remove(key);
                }
            }

            return kept.Count == 0 ? null : kept;
        }

        /// <summary>The matched count, then each other count that is not zero.</summary>
        private string Summarize(LinkedFileApplyReport report, int unmatchedRows, int unmatchedAchievements)
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
            Add("LOCPlayAch_LinkedFile_Count_UnmatchedAchievements", unmatchedAchievements);
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
                RequestClose?.Invoke(this, EventArgs.Empty);
                return;
            }

            var lines = new List<string>
            {
                report.Status == LinkedFileReadStatus.Ok
                    ? L("LOCPlayAch_LinkedFile_ApplyReportTitle")
                    : DescribeStatus(report.Status),
                Summarize(report, report.UnmatchedRows, report.UnmatchedAchievements)
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
                case LinkedFileSchemaProblem.NoMatchColumn:
                    return L("LOCPlayAch_LinkedFile_Problem_NoMatch");
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
