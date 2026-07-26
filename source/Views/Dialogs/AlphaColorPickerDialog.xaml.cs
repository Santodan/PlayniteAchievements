using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Playnite.SDK;

namespace PlayniteAchievements.Views.Dialogs
{
    public partial class AlphaColorPickerDialog : UserControl
    {
        private bool _isUpdating;
        private bool _isColorSpaceMouseDown;
        private double _hue;
        private double _saturation;
        private double _value;
        private Color _selectedColor;

        public bool? DialogResult { get; private set; }
        public event EventHandler RequestClose;

        public AlphaColorPickerDialog()
        {
            InitializeComponent();
            UpdateHsvFromRgb(Colors.White.R, Colors.White.G, Colors.White.B);
            SelectedColor = Colors.White;
        }

        public Color SelectedColor
        {
            get => _selectedColor;
            set
            {
                _selectedColor = value;
                UpdateControlsFromColor();
            }
        }

        public string SelectedColorText => ToColorText(SelectedColor);

        public void SetInitialColor(string currentValue)
        {
            if (TryParseColor(currentValue, out var color))
            {
                UpdateHsvFromRgb(color.R, color.G, color.B);
                SelectedColor = color;
            }
        }

        public static bool TryParseColor(string value, out Color color)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    color = Colors.Transparent;
                    return false;
                }

                color = (Color)ColorConverter.ConvertFromString(value.Trim());
                return true;
            }
            catch
            {
                color = Colors.Transparent;
                return false;
            }
        }

        public static string NormalizeColorText(string value)
        {
            return TryParseColor(value, out var color)
                ? ToColorText(color)
                : value;
        }

        private static string ToColorText(Color color)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "#{0:X2}{1:X2}{2:X2}{3:X2}",
                color.A,
                color.R,
                color.G,
                color.B);
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            HexTextBox.Focus();
            HexTextBox.SelectAll();
        }

        private void ChannelSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdating || !IsLoaded)
            {
                return;
            }

            UpdateHsvFromRgb(ToByte(RedSlider.Value), ToByte(GreenSlider.Value), ToByte(BlueSlider.Value));
            SelectedColor = Color.FromArgb(
                ToByte(AlphaSlider.Value),
                ToByte(RedSlider.Value),
                ToByte(GreenSlider.Value),
                ToByte(BlueSlider.Value));
        }

        private void HueSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdating || !IsLoaded)
            {
                return;
            }

            _hue = HueSlider.Value;
            UpdateColorFromHsv();
        }

        private void ColorSpace_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isColorSpaceMouseDown = true;
            ColorSpace.CaptureMouse();
            UpdateColorSpaceSelection(e.GetPosition(ColorSpace));
        }

        private void ColorSpace_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isColorSpaceMouseDown)
            {
                UpdateColorSpaceSelection(e.GetPosition(ColorSpace));
            }
        }

        private void ColorSpace_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isColorSpaceMouseDown)
            {
                return;
            }

            _isColorSpaceMouseDown = false;
            ColorSpace.ReleaseMouseCapture();
            UpdateColorSpaceSelection(e.GetPosition(ColorSpace));
        }

        private void ColorSpace_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateColorSpaceMarker();
        }

        private void ChannelTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            CommitChannelTextBoxes();
        }

        private void ChannelTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                CommitChannelTextBoxes();
                e.Handled = true;
            }
        }

        private void HexTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdating || !IsLoaded)
            {
                return;
            }

            if (TryParseColor(HexTextBox.Text, out var color))
            {
                ValidationText.Text = string.Empty;
                UpdateHsvFromRgb(color.R, color.G, color.B);
                SelectedColor = color;
            }
            else
            {
                ValidationText.Text = ResourceProvider.GetString("LOCPlayAch_ColorPicker_InvalidColor");
            }
        }

        private void HexTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && TryParseColor(HexTextBox.Text, out var color))
            {
                UpdateHsvFromRgb(color.R, color.G, color.B);
                SelectedColor = color;
                e.Handled = true;
            }
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            if (!TryParseColor(HexTextBox.Text, out var color))
            {
                ValidationText.Text = ResourceProvider.GetString("LOCPlayAch_ColorPicker_InvalidColor");
                return;
            }

            UpdateHsvFromRgb(color.R, color.G, color.B);
            SelectedColor = color;
            DialogResult = true;
            RequestClose?.Invoke(this, EventArgs.Empty);
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            RequestClose?.Invoke(this, EventArgs.Empty);
        }

        private void CommitChannelTextBoxes()
        {
            if (_isUpdating)
            {
                return;
            }

            var color = Color.FromArgb(
                ParseChannel(AlphaTextBox.Text, SelectedColor.A),
                ParseChannel(RedTextBox.Text, SelectedColor.R),
                ParseChannel(GreenTextBox.Text, SelectedColor.G),
                ParseChannel(BlueTextBox.Text, SelectedColor.B));
            UpdateHsvFromRgb(color.R, color.G, color.B);
            SelectedColor = color;
        }

        private void UpdateControlsFromColor()
        {
            if (!IsInitialized)
            {
                return;
            }

            _isUpdating = true;
            try
            {
                AlphaSlider.Value = SelectedColor.A;
                RedSlider.Value = SelectedColor.R;
                GreenSlider.Value = SelectedColor.G;
                BlueSlider.Value = SelectedColor.B;
                HueSlider.Value = _hue;

                AlphaTextBox.Text = SelectedColor.A.ToString(CultureInfo.InvariantCulture);
                RedTextBox.Text = SelectedColor.R.ToString(CultureInfo.InvariantCulture);
                GreenTextBox.Text = SelectedColor.G.ToString(CultureInfo.InvariantCulture);
                BlueTextBox.Text = SelectedColor.B.ToString(CultureInfo.InvariantCulture);
                HexTextBox.Text = SelectedColorText;
                PreviewSwatch.Background = new SolidColorBrush(SelectedColor);
                HueSurface.Background = new SolidColorBrush(ColorFromHsv(_hue, 1, 1));
                UpdateColorSpaceMarker();
                ValidationText.Text = string.Empty;
            }
            finally
            {
                _isUpdating = false;
            }
        }

        private void UpdateColorSpaceSelection(Point point)
        {
            var width = Math.Max(1, ColorSpace.ActualWidth);
            var height = Math.Max(1, ColorSpace.ActualHeight);
            _saturation = Clamp(point.X / width, 0, 1);
            _value = 1 - Clamp(point.Y / height, 0, 1);
            UpdateColorFromHsv();
        }

        private void UpdateColorFromHsv()
        {
            var rgb = ColorFromHsv(_hue, _saturation, _value);
            SelectedColor = Color.FromArgb(SelectedColor.A, rgb.R, rgb.G, rgb.B);
        }

        private void UpdateColorSpaceMarker()
        {
            if (ColorSpaceMarker == null || ColorSpace == null)
            {
                return;
            }

            var x = (_saturation * Math.Max(1, ColorSpace.ActualWidth)) - (ColorSpaceMarker.Width / 2);
            var y = ((1 - _value) * Math.Max(1, ColorSpace.ActualHeight)) - (ColorSpaceMarker.Height / 2);
            Canvas.SetLeft(ColorSpaceMarker, x);
            Canvas.SetTop(ColorSpaceMarker, y);
        }

        private void UpdateHsvFromRgb(byte red, byte green, byte blue)
        {
            RgbToHsv(red, green, blue, out _hue, out _saturation, out _value);
        }

        private static byte ParseChannel(string value, byte fallback)
        {
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                return fallback;
            }

            return (byte)Math.Max(0, Math.Min(255, parsed));
        }

        private static byte ToByte(double value)
        {
            return (byte)Math.Max(0, Math.Min(255, (int)Math.Round(value)));
        }

        private static Color ColorFromHsv(double hue, double saturation, double value)
        {
            hue = ((hue % 360) + 360) % 360;
            saturation = Clamp(saturation, 0, 1);
            value = Clamp(value, 0, 1);

            var chroma = value * saturation;
            var x = chroma * (1 - Math.Abs(((hue / 60) % 2) - 1));
            var match = value - chroma;

            double red;
            double green;
            double blue;
            if (hue < 60)
            {
                red = chroma;
                green = x;
                blue = 0;
            }
            else if (hue < 120)
            {
                red = x;
                green = chroma;
                blue = 0;
            }
            else if (hue < 180)
            {
                red = 0;
                green = chroma;
                blue = x;
            }
            else if (hue < 240)
            {
                red = 0;
                green = x;
                blue = chroma;
            }
            else if (hue < 300)
            {
                red = x;
                green = 0;
                blue = chroma;
            }
            else
            {
                red = chroma;
                green = 0;
                blue = x;
            }

            return Color.FromRgb(
                ToByte((red + match) * 255),
                ToByte((green + match) * 255),
                ToByte((blue + match) * 255));
        }

        private static void RgbToHsv(byte red, byte green, byte blue, out double hue, out double saturation, out double value)
        {
            var r = red / 255.0;
            var g = green / 255.0;
            var b = blue / 255.0;
            var max = Math.Max(r, Math.Max(g, b));
            var min = Math.Min(r, Math.Min(g, b));
            var delta = max - min;

            if (delta == 0)
            {
                hue = 0;
            }
            else if (max == r)
            {
                hue = 60 * (((g - b) / delta) % 6);
            }
            else if (max == g)
            {
                hue = 60 * (((b - r) / delta) + 2);
            }
            else
            {
                hue = 60 * (((r - g) / delta) + 4);
            }

            if (hue < 0)
            {
                hue += 360;
            }

            saturation = max == 0 ? 0 : delta / max;
            value = max;
        }

        private static double Clamp(double value, double minimum, double maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }
    }
}
