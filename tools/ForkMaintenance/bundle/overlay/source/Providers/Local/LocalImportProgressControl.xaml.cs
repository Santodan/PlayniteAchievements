using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;

namespace PlayniteAchievements.Providers.Local
{
    public partial class LocalImportProgressControl : UserControl, INotifyPropertyChanged
    {
        private string _dialogTitle = "Importing Games";
        private string _progressMessage = "Preparing Local import...";
        private string _detailMessage = "";
        private string _copyableReportText = "";
        private double _progressPercent;
        private bool _showCancelButton = true;
        private bool _showCloseButton;

        public event PropertyChangedEventHandler PropertyChanged;
        public event EventHandler CancelRequested;
        public event EventHandler RequestClose;

        public string DialogTitle
        {
            get => _dialogTitle;
            set
            {
                if (string.Equals(_dialogTitle, value, StringComparison.Ordinal))
                {
                    return;
                }

                _dialogTitle = value ?? string.Empty;
                OnPropertyChanged();
            }
        }

        public string ProgressMessage
        {
            get => _progressMessage;
            set
            {
                if (string.Equals(_progressMessage, value, StringComparison.Ordinal))
                {
                    return;
                }

                _progressMessage = value ?? string.Empty;
                OnPropertyChanged();
            }
        }

        public string DetailMessage
        {
            get => _detailMessage;
            set
            {
                if (string.Equals(_detailMessage, value, StringComparison.Ordinal))
                {
                    return;
                }

                _detailMessage = value ?? string.Empty;
                OnPropertyChanged();
            }
        }

        public double ProgressPercent
        {
            get => _progressPercent;
            set
            {
                if (Math.Abs(_progressPercent - value) < 0.01d)
                {
                    return;
                }

                _progressPercent = Math.Max(0d, Math.Min(100d, value));
                OnPropertyChanged();
            }
        }

        public string CopyableReportText
        {
            get => _copyableReportText;
            set
            {
                if (string.Equals(_copyableReportText, value, StringComparison.Ordinal))
                {
                    return;
                }

                _copyableReportText = value ?? string.Empty;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ShowCopyableReport));
            }
        }

        public bool ShowCopyableReport => !string.IsNullOrWhiteSpace(CopyableReportText);

        public bool ShowCancelButton
        {
            get => _showCancelButton;
            set
            {
                if (_showCancelButton == value)
                {
                    return;
                }

                _showCancelButton = value;
                OnPropertyChanged();
            }
        }

        public bool ShowCloseButton
        {
            get => _showCloseButton;
            set
            {
                if (_showCloseButton == value)
                {
                    return;
                }

                _showCloseButton = value;
                OnPropertyChanged();
            }
        }

        public LocalImportProgressControl()
        {
            InitializeComponent();
            DataContext = this;
        }

        public void Update(double percent, string message, string detail)
        {
            ProgressMessage = message;
            DetailMessage = detail;
            ProgressPercent = percent;
        }

        public void MarkCompleted(string message)
        {
            ProgressMessage = message;
            DetailMessage = "Import finished.";
            ProgressPercent = 100d;
            ShowCancelButton = false;
            ShowCloseButton = true;
        }

        public void MarkCancelled(string message)
        {
            ProgressMessage = message;
            DetailMessage = "The import was cancelled before it finished.";
            ShowCancelButton = false;
            ShowCloseButton = true;
        }

        public void MarkFailed(string message)
        {
            ProgressMessage = message;
            ShowCancelButton = false;
            ShowCloseButton = true;
        }

        public void SetCopyableReport(string reportText)
        {
            CopyableReportText = reportText ?? string.Empty;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            CancelRequested?.Invoke(this, EventArgs.Empty);
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            RequestClose?.Invoke(this, EventArgs.Empty);
        }

        private void CopyReportButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(CopyableReportText))
            {
                return;
            }

            try
            {
                Clipboard.SetText(CopyableReportText);
            }
            catch
            {
            }
        }

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}