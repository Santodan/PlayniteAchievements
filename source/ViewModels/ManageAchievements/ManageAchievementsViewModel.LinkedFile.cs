using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.LinkedFiles;
using System;
using System.Windows;

namespace PlayniteAchievements.ViewModels.ManageAchievements
{
    /// <summary>The Overview line for a game's linked file, and the prompt after a package import.</summary>
    public sealed partial class ManageAchievementsViewModel
    {
        private Func<bool> _showLinkedFileDialog;
        private RelayCommand _openLinkedFileCommand;
        private LinkedFileLink _linkedFile;

        public bool HasLinkedFile => _linkedFile != null;

        public string LinkedFileSummary => LinkedFileStatusText.Summarize(_linkedFile, L);

        /// <summary>"Attach file" for a mapping without a file, "Mapping" otherwise.</summary>
        public string LinkedFileActionText => _linkedFile != null && !_linkedFile.HasFilePath
            ? L("LOCPlayAch_LinkedFile_AttachFile")
            : L("LOCPlayAch_LinkedFile_Mapping");

        public RelayCommand OpenLinkedFileCommand =>
            _openLinkedFileCommand ??= new RelayCommand(_ => OpenLinkedFile(), _ => _showLinkedFileDialog != null && HasGame);

        /// <summary>Hands the view model the host's Link File window.</summary>
        internal void AttachLinkedFileDialog(Func<bool> show)
        {
            _showLinkedFileDialog = show;
            OpenLinkedFileCommand.RaiseCanExecuteChanged();
        }

        private void OpenLinkedFile()
        {
            if (_showLinkedFileDialog?.Invoke() == true)
            {
                NotifyCustomDataChanged(requiresRefresh: false);
            }

            RefreshLinkedFile();
        }

        private void RefreshLinkedFile()
        {
            _linkedFile = HasGame ? _plugin?.GameCustomDataStore?.LoadOrDefault(_gameId)?.LinkedFile : null;
            OnPropertyChanged(nameof(HasLinkedFile));
            OnPropertyChanged(nameof(LinkedFileSummary));
            OnPropertyChanged(nameof(LinkedFileActionText));
        }

        /// <summary>
        /// After a package import: a package carries the file mapping but never the file, so a
        /// game that now has a mapping and no file is offered the window to attach one.
        /// </summary>
        private void PromptAttachLinkedFileIfMappingOnly()
        {
            RefreshLinkedFile();
            if (_linkedFile == null || _linkedFile.HasFilePath || _showLinkedFileDialog == null)
            {
                return;
            }

            var answer = _playniteApi?.Dialogs?.ShowMessage(
                L("LOCPlayAch_LinkedFile_ImportedSchemaPrompt"),
                L("LOCPlayAch_Title_PluginName"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (answer == MessageBoxResult.Yes)
            {
                OpenLinkedFile();
            }
        }
    }
}
