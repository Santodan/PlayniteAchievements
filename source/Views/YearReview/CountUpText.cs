using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using PlayniteAchievements.Common;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Views.YearReview
{
    /// <summary>How <see cref="CountUpText"/> writes its number.</summary>
    public enum CountUpFormat
    {
        /// <summary>A count, with the culture's group separators.</summary>
        Number,

        /// <summary>A 0-100 percent, through <see cref="PercentFormatter"/>.</summary>
        Percent
    }

    /// <summary>
    /// A number that counts from <see cref="From"/> to <see cref="Value"/> when its slide plays.
    /// At rest it shows <see cref="Value"/>, so it reads correctly with motion off.
    /// </summary>
    public sealed class CountUpText : TextBlock
    {
        private const double DurationMs = 1200;

        public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
            nameof(Value), typeof(double), typeof(CountUpText), new PropertyMetadata(0d, OnTextInputChanged));

        public double Value
        {
            get => (double)GetValue(ValueProperty);
            set => SetValue(ValueProperty, value);
        }

        public static readonly DependencyProperty FromProperty = DependencyProperty.Register(
            nameof(From), typeof(double), typeof(CountUpText), new PropertyMetadata(0d));

        public double From
        {
            get => (double)GetValue(FromProperty);
            set => SetValue(FromProperty, value);
        }

        public static readonly DependencyProperty DecimalsProperty = DependencyProperty.Register(
            nameof(Decimals), typeof(int), typeof(CountUpText), new PropertyMetadata(0, OnTextInputChanged));

        public int Decimals
        {
            get => (int)GetValue(DecimalsProperty);
            set => SetValue(DecimalsProperty, value);
        }

        public static readonly DependencyProperty FormatProperty = DependencyProperty.Register(
            nameof(Format), typeof(CountUpFormat), typeof(CountUpText), new PropertyMetadata(CountUpFormat.Number, OnTextInputChanged));

        public CountUpFormat Format
        {
            get => (CountUpFormat)GetValue(FormatProperty);
            set => SetValue(FormatProperty, value);
        }

        public static readonly DependencyProperty PrefixProperty = DependencyProperty.Register(
            nameof(Prefix), typeof(string), typeof(CountUpText), new PropertyMetadata(null, OnTextInputChanged));

        /// <summary>Text before the number, such as a plus sign.</summary>
        public string Prefix
        {
            get => (string)GetValue(PrefixProperty);
            set => SetValue(PrefixProperty, value);
        }

        private static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
            "Progress", typeof(double), typeof(CountUpText), new PropertyMetadata(1d, OnTextInputChanged));

        public CountUpText()
        {
            Loaded += (s, e) => UpdateText();
        }

        /// <summary>Counts up after <paramref name="delay"/> milliseconds.</summary>
        public void Play(double delay)
        {
            if (!YearReviewMotion.Enabled)
            {
                Stop();
                return;
            }

            var animation = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
            animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(Math.Max(0, delay)))));
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(
                1,
                KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(Math.Max(0, delay) + DurationMs)),
                new CubicEase { EasingMode = EasingMode.EaseOut }));
            BeginAnimation(ProgressProperty, AnimationFrameRate.Apply(animation));
        }

        /// <summary>Shows the final value.</summary>
        public void Stop() => BeginAnimation(ProgressProperty, null);

        private static void OnTextInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
            ((CountUpText)d).UpdateText();

        private void UpdateText()
        {
            var progress = (double)GetValue(ProgressProperty);
            var value = From + (Value - From) * progress;
            var number = Format == CountUpFormat.Percent
                ? PercentFormatter.Format(value, Decimals)
                : value.ToString("N" + Decimals, FormattingCulture.Current);
            Text = (Prefix ?? string.Empty) + number;
        }
    }
}
