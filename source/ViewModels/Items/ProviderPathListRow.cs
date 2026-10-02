using PlayniteAchievements.Common;

namespace PlayniteAchievements.ViewModels.Items
{
    /// <summary>
    /// One editable row in a provider path list editor.
    /// </summary>
    public sealed class ProviderPathListRow : ObservableObject
    {
        private string _path;
        private bool _isValid;
        private bool _hasStatus;
        private string _statusText;

        public ProviderPathListRow(string path)
        {
            _path = path ?? string.Empty;
        }

        public string Path
        {
            get => _path;
            set => SetValue(ref _path, value ?? string.Empty);
        }

        public bool IsValid
        {
            get => _isValid;
            set => SetValue(ref _isValid, value);
        }

        /// <summary>
        /// False for a blank row, which shows no status glyph.
        /// </summary>
        public bool HasStatus
        {
            get => _hasStatus;
            set => SetValue(ref _hasStatus, value);
        }

        public string StatusText
        {
            get => _statusText;
            set => SetValue(ref _statusText, value);
        }
    }
}
