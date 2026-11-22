using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.LinkedFiles;
using System;
using RelayCommand = PlayniteAchievements.Common.RelayCommand;

namespace PlayniteAchievements.ViewModels.ManageAchievements
{
    /// <summary>The Editor header's linked file button and its state.</summary>
    public sealed partial class ManageAchievementsEditorViewModel
    {
        private Func<bool> _showLinkedFileDialog;
        private RelayCommand _openLinkedFileCommand;
        private LinkedFileLink _linkedFile;

        /// <summary>Opens the Link File window; true when the link changed. Set by the host.</summary>
        internal Func<bool> ShowLinkedFileDialog
        {
            get => _showLinkedFileDialog;
            set
            {
                _showLinkedFileDialog = value;
                RefreshLinkedFileState();
            }
        }

        public RelayCommand OpenLinkedFileCommand =>
            _openLinkedFileCommand ??= new RelayCommand(_ => OpenLinkedFileDialog(), _ => _showLinkedFileDialog != null);

        public bool HasLinkedFile => _linkedFile != null;

        /// <summary>
        /// Whether the link buttons show: a host that opens the Link File window, over one game.
        /// The library editor across games links nothing.
        /// </summary>
        private bool CanLinkFile => _showLinkedFileDialog != null && AllowsSingleGameFeatures;

        /// <summary>The plain Link File button; a linked game shows Edit Linked File instead.</summary>
        public bool ShowLinkFileButton => CanLinkFile && !HasLinkedFile;

        /// <summary>The Edit Linked File button, for a linked game.</summary>
        public bool ShowEditLinkedFileButton => CanLinkFile && HasLinkedFile;

        /// <summary>Marks the Edit Linked File button with a warning when the link cannot be read.</summary>
        public bool LinkedFileHasProblem => LinkedFileStatusText.HasProblem(_linkedFile);

        /// <summary>The file's path, its state, and that a read replaces edits made here.</summary>
        public string LinkedFileToolTip =>
            _linkedFile == null
                ? null
                : (_linkedFile.HasFilePath ? _linkedFile.FilePath + Environment.NewLine : string.Empty) +
                  LinkedFileStatusText.DescribeState(_linkedFile, Localize) + Environment.NewLine + Environment.NewLine +
                  Localize("LOCPlayAch_LinkedFile_OverwriteHint");

        internal void RefreshLinkedFileState()
        {
            var record = _gameCustomDataStore?.LoadOrDefault(_gameId);
            _linkedFile = record?.LinkedFile;
            OpenLinkedFileCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(HasLinkedFile));
            OnPropertyChanged(nameof(ShowLinkFileButton));
            OnPropertyChanged(nameof(ShowEditLinkedFileButton));
            OnPropertyChanged(nameof(LinkedFileHasProblem));
            OnPropertyChanged(nameof(LinkedFileToolTip));
        }

        /// <summary>
        /// Opens the Link File window, then reloads so what an Apply Now wrote shows in the grid.
        /// Pending unlock edits are flushed first, as for the definitions link, so a read never
        /// lands on a link a staged edit is about to rewrite.
        /// </summary>
        private void OpenLinkedFileDialog()
        {
            if (_showLinkedFileDialog == null)
            {
                return;
            }

            FlushManualUnlocks();
            if (_showLinkedFileDialog())
            {
                ReloadData();
                RaiseAssignmentsChanged();
            }

            RefreshLinkedFileState();
        }

        private static string Localize(string key)
        {
            var value = ResourceProvider.GetString(key);
            return string.IsNullOrEmpty(value) ? key : value;
        }
    }
}
