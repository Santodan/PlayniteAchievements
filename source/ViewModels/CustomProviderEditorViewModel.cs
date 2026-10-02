using Microsoft.Win32;
using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.CustomProviders;
using PlayniteAchievements.Views.Converters;
using System;
using System.Threading.Tasks;
using System.Windows.Media;
using ObservableObject = PlayniteAchievements.Common.ObservableObject;
using RelayCommand = PlayniteAchievements.Common.RelayCommand;

namespace PlayniteAchievements.ViewModels
{
    public enum CustomProviderEditorResult
    {
        Cancelled,
        Saved,
        Deleted
    }

    /// <summary>
    /// Working copy of one custom provider edited in its own window. Nothing is persisted until
    /// the caller confirms; the SVG import runs while the window is open so the preview reflects
    /// the chosen icon before saving.
    /// </summary>
    public sealed class CustomProviderEditorViewModel : ObservableObject
    {
        private readonly Func<string, string> _pickColor;
        private readonly ILogger _logger;
        private string _name;
        private string _colorHex;
        private string _iconSource;
        private string _iconPathData;
        private bool _isImporting;
        private string _statusText;
        private bool _statusIsError;

        public CustomProviderEditorViewModel(
            CustomProviderDefinition existing,
            Func<string, string> pickColor,
            ILogger logger = null)
        {
            _pickColor = pickColor;
            _logger = logger;
            IsNew = existing == null;
            Id = existing?.Id;
            _name = existing?.Name ?? string.Empty;
            _colorHex = string.IsNullOrWhiteSpace(existing?.ColorHex) ? CustomProviderKeys.DefaultColorHex : existing.ColorHex;
            _iconSource = existing?.IconSource;
            _iconPathData = existing?.IconPathData;

            PickColorCommand = new RelayCommand(_ => PickColor(), _ => _pickColor != null);
            BrowseCommand = new RelayCommand(_ => Browse(), _ => !IsImporting);
            ClearIconCommand = new RelayCommand(_ => ClearIcon(), _ => HasCustomIcon && !IsImporting);
        }

        public bool IsNew { get; }

        public string Id { get; }

        public RelayCommand PickColorCommand { get; }

        public RelayCommand BrowseCommand { get; }

        public RelayCommand ClearIconCommand { get; }

        public string Name
        {
            get => _name;
            set
            {
                if (SetValueAndReturn(ref _name, value))
                {
                    OnPropertyChanged(nameof(CanConfirm));
                }
            }
        }

        public string ColorHex
        {
            get => _colorHex;
            set
            {
                if (!SetValueAndReturn(ref _colorHex, value))
                {
                    return;
                }

                SetStatus(
                    CustomProviderStore.IsValidColor(value)
                        ? null
                        : ResourceProvider.GetString("LOCPlayAch_ManageAchievements_Custom_ProviderInvalidColor"),
                    true);
                OnPropertyChanged(nameof(ColorBrush));
                OnPropertyChanged(nameof(PreviewImage));
                OnPropertyChanged(nameof(CanConfirm));
            }
        }

        /// <summary>
        /// Link or local path of the SVG. Committing a value imports it; a blank value returns the
        /// provider to the default icon.
        /// </summary>
        public string IconSource
        {
            get => _iconSource;
            set
            {
                var source = NormalizeText(value);
                if (string.Equals(source, NormalizeText(_iconSource), StringComparison.Ordinal))
                {
                    OnPropertyChanged(nameof(IconSource));
                    return;
                }

                if (source == null)
                {
                    ClearIcon();
                    return;
                }

                _ = ImportIconSourceAsync(source);
            }
        }

        public string IconPathData => _iconPathData;

        public bool HasCustomIcon => !string.IsNullOrWhiteSpace(_iconPathData);

        public bool IsImporting
        {
            get => _isImporting;
            private set
            {
                if (SetValueAndReturn(ref _isImporting, value))
                {
                    OnPropertyChanged(nameof(CanConfirm));
                    BrowseCommand.RaiseCanExecuteChanged();
                    ClearIconCommand.RaiseCanExecuteChanged();
                }
            }
        }

        public bool CanConfirm =>
            !string.IsNullOrWhiteSpace(Name) &&
            CustomProviderStore.IsValidColor(ColorHex) &&
            !IsImporting;

        public string StatusText
        {
            get => _statusText;
            private set
            {
                if (SetValueAndReturn(ref _statusText, value))
                {
                    OnPropertyChanged(nameof(HasStatusText));
                }
            }
        }

        public bool HasStatusText => !string.IsNullOrWhiteSpace(StatusText);

        public bool StatusIsError
        {
            get => _statusIsError;
            private set => SetValue(ref _statusIsError, value);
        }

        public Brush ColorBrush
        {
            get
            {
                try
                {
                    if (CustomProviderStore.IsValidColor(ColorHex) &&
                        ColorConverter.ConvertFromString(ColorHex.Trim()) is Color color)
                    {
                        var brush = new SolidColorBrush(color);
                        brush.Freeze();
                        return brush;
                    }
                }
                catch
                {
                    // Fall through to transparent.
                }

                return Brushes.Transparent;
            }
        }

        /// <summary>
        /// The icon as it will render: the imported geometry tinted with the chosen color, or the
        /// default icon in that color while no SVG has been imported.
        /// </summary>
        public ImageSource PreviewImage
        {
            get
            {
                var colorHex = CustomProviderStore.IsValidColor(ColorHex) ? ColorHex.Trim() : CustomProviderKeys.DefaultColorHex;
                if (HasCustomIcon)
                {
                    try
                    {
                        var geometry = Geometry.Parse(_iconPathData);
                        var color = (Color)ColorConverter.ConvertFromString(colorHex);
                        var image = new DrawingImage(new GeometryDrawing(new SolidColorBrush(color), null, geometry));
                        image.Freeze();
                        return image;
                    }
                    catch (Exception ex)
                    {
                        _logger?.Debug(ex, "Custom provider preview geometry failed to render.");
                    }
                }

                return ProviderIconConverter.BuildIcon(CustomProviderKeys.BaseIconKey, colorHex);
            }
        }

        public CustomProviderDefinition BuildDefinition()
        {
            return new CustomProviderDefinition
            {
                Id = Id,
                Name = NormalizeText(Name),
                ColorHex = CustomProviderStore.IsValidColor(ColorHex) ? ColorHex.Trim() : CustomProviderKeys.DefaultColorHex,
                IconPathData = _iconPathData,
                IconSource = HasCustomIcon ? NormalizeText(_iconSource) : null
            };
        }

        private void PickColor()
        {
            if (_pickColor == null)
            {
                return;
            }

            var color = _pickColor(ColorHex);
            if (!string.IsNullOrWhiteSpace(color))
            {
                ColorHex = color;
            }
        }

        private void Browse()
        {
            var dialog = new OpenFileDialog
            {
                Filter = "SVG Files (*.svg)|*.svg|All Files (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };

            if (dialog.ShowDialog() == true)
            {
                IconSource = dialog.FileName;
            }
        }

        private void ClearIcon()
        {
            _iconPathData = null;
            _iconSource = null;
            SetStatus(null, false);
            RaiseIconChanged();
        }

        private async Task ImportIconSourceAsync(string source)
        {
            IsImporting = true;
            try
            {
                var pathData = await Task.Run(async () =>
                {
                    if (source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                        source.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        var markup = await HttpClientFactory.Shared.GetStringAsync(source).ConfigureAwait(false);
                        return SvgGeometryImporter.ImportMarkup(markup);
                    }

                    return SvgGeometryImporter.ImportFile(source);
                }).ConfigureAwait(true);

                _iconPathData = pathData;
                _iconSource = source;
                SetStatus(null, false);
            }
            catch (SvgGeometryImportException ex)
            {
                _logger?.Warn(ex, $"Failed importing SVG '{source}' for a custom provider.");
                SetStatus(
                    ResourceProvider.GetString(ex.NoDrawableShapes
                        ? "LOCPlayAch_ManageAchievements_Custom_ProviderSvgNoShapes"
                        : "LOCPlayAch_ManageAchievements_Custom_ProviderSvgInvalid"),
                    true);
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"Failed loading SVG '{source}' for a custom provider.");
                SetStatus(string.Format(ResourceProvider.GetString("LOCPlayAch_Status_Failed"), ex.Message), true);
            }
            finally
            {
                IsImporting = false;
                RaiseIconChanged();
            }
        }

        private void RaiseIconChanged()
        {
            OnPropertyChanged(nameof(IconSource));
            OnPropertyChanged(nameof(IconPathData));
            OnPropertyChanged(nameof(HasCustomIcon));
            OnPropertyChanged(nameof(PreviewImage));
            ClearIconCommand.RaiseCanExecuteChanged();
        }

        private void SetStatus(string value, bool isError)
        {
            StatusText = value;
            StatusIsError = isError && !string.IsNullOrWhiteSpace(value);
        }

        private static string NormalizeText(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
        }
    }
}
