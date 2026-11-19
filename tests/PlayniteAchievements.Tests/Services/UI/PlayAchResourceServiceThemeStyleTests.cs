using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.UI;
using PlayniteAchievements.Tests.TestInfrastructure;

namespace PlayniteAchievements.Tests.Services.UI
{
    /// <summary>
    /// Theme brushes read from the theme's implicit control styles. The theme dictionaries are parsed
    /// from loose XAML, the way Playnite loads theme files.
    /// </summary>
    [TestClass]
    public class PlayAchResourceServiceThemeStyleTests
    {
        private const string XamlHeader =
            "<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
            "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">";

        // Shaped like NovaX: NormalBrush is a hover color, and the controls use keys of their own.
        private const string NovaXLikeTheme = XamlHeader + @"
            <SolidColorBrush x:Key=""TextBrush"" Color=""#FFFFFFFF""/>
            <SolidColorBrush x:Key=""NormalBrush"" Color=""#FF48494B""/>
            <SolidColorBrush x:Key=""NormalBorderBrush"" Color=""Transparent""/>
            <SolidColorBrush x:Key=""NormalBorderBrushControls"" Color=""#FF6F6D70""/>
            <SolidColorBrush x:Key=""ThumbBrush"" Color=""#FF5F6062""/>
            <Style TargetType=""ComboBox"">
                <Setter Property=""BorderBrush"" Value=""{DynamicResource NormalBorderBrushControls}""/>
                <Style.Triggers>
                    <Trigger Property=""IsMouseOver"" Value=""True"">
                        <Setter Property=""BorderBrush"" Value=""{DynamicResource TextBrush}""/>
                    </Trigger>
                </Style.Triggers>
            </Style>
            <Style TargetType=""Button"">
                <Setter Property=""BorderBrush"" Value=""{DynamicResource NormalBorderBrush}""/>
            </Style>
            <Style TargetType=""Thumb"">
                <Setter Property=""Background"" Value=""{DynamicResource ThumbBrush}""/>
            </Style>
            <Style TargetType=""TextBlock"">
                <Setter Property=""Foreground"" Value=""{DynamicResource TextBrush}""/>
            </Style>
        </ResourceDictionary>";

        [TestMethod]
        public void ReadThemeStyleValue_ResolvesTheDynamicResourceOfAPlainSetter()
        {
            StaTestHost.Run(() =>
            {
                var lookup = LookupIn(Parse(NovaXLikeTheme));

                var value = PlayAchResourceService.ReadThemeStyleValue(typeof(ComboBox), Control.BorderBrushProperty, lookup);

                AssertColor("#FF6F6D70", value);
            });
        }

        [TestMethod]
        public void ReadThemeStyleValue_ReadsLiteralAndStaticResourceSetters()
        {
            StaTestHost.Run(() =>
            {
                var lookup = LookupIn(Parse(XamlHeader + @"
                    <SolidColorBrush x:Key=""Outline"" Color=""#FF102030""/>
                    <Style TargetType=""ComboBox"">
                        <Setter Property=""BorderBrush"" Value=""{StaticResource Outline}""/>
                    </Style>
                    <Style TargetType=""Button"">
                        <Setter Property=""BorderBrush"" Value=""#FF405060""/>
                    </Style>
                </ResourceDictionary>"));

                AssertColor("#FF102030", PlayAchResourceService.ReadThemeStyleValue(typeof(ComboBox), Control.BorderBrushProperty, lookup));
                AssertColor("#FF405060", PlayAchResourceService.ReadThemeStyleValue(typeof(Button), Control.BorderBrushProperty, lookup));
            });
        }

        [TestMethod]
        public void ReadThemeStyleValue_FollowsBasedOn()
        {
            StaTestHost.Run(() =>
            {
                var lookup = LookupIn(Parse(XamlHeader + @"
                    <SolidColorBrush x:Key=""Outline"" Color=""#FF102030""/>
                    <Style x:Key=""BaseComboBox"" TargetType=""ComboBox"">
                        <Setter Property=""BorderBrush"" Value=""{DynamicResource Outline}""/>
                    </Style>
                    <Style TargetType=""ComboBox"" BasedOn=""{StaticResource BaseComboBox}"">
                        <Setter Property=""Padding"" Value=""4""/>
                    </Style>
                </ResourceDictionary>"));

                AssertColor("#FF102030", PlayAchResourceService.ReadThemeStyleValue(typeof(ComboBox), Control.BorderBrushProperty, lookup));
            });
        }

        [TestMethod]
        public void ReadThemeStyleValue_IgnoresTriggerOnlySetters()
        {
            StaTestHost.Run(() =>
            {
                // Shaped like Dune: the outline is set only inside a day/night trigger.
                var lookup = LookupIn(Parse(XamlHeader + @"
                    <SolidColorBrush x:Key=""Outline"" Color=""#FF102030""/>
                    <Style TargetType=""ComboBox"">
                        <Style.Triggers>
                            <Trigger Property=""IsEnabled"" Value=""True"">
                                <Setter Property=""BorderBrush"" Value=""{DynamicResource Outline}""/>
                            </Trigger>
                        </Style.Triggers>
                    </Style>
                </ResourceDictionary>"));

                Assert.IsNull(PlayAchResourceService.ReadThemeStyleValue(typeof(ComboBox), Control.BorderBrushProperty, lookup));
                Assert.IsNull(PlayAchResourceService.ReadThemeStyleValue(typeof(Button), Control.BorderBrushProperty, lookup));
            });
        }

        [TestMethod]
        public void Apply_ReadsInputButtonAndThumbBrushesFromTheThemeStyles()
        {
            StaTestHost.Run(() =>
            {
                var resources = new ResourceDictionary();

                PlayAchResourceService.Apply(resources, NoOverrides(), null, LookupIn(Parse(NovaXLikeTheme)));

                AssertColor("#FF48494B", resources["PlayAch.Brush.ControlBorder"]);
                AssertColor("#FF6F6D70", resources["PlayAch.Brush.Input.Border"]);
                AssertColor("#00FFFFFF", resources["PlayAch.Brush.Button.Border"]);
                AssertColor("#FF5F6062", resources["PlayAch.Brush.ScrollBar.Thumb"]);
                AssertColor("#FF48494B", resources["PlayAch.Brush.Chart.Separator"]);
            });
        }

        [TestMethod]
        public void Apply_ControlBorderOverrideStillDrivesTheStyledAliases()
        {
            StaTestHost.Run(() =>
            {
                var resources = new ResourceDictionary();
                var overrides = NoOverrides();
                overrides["PlayAch.Brush.ControlBorder"] = new ResourceOverrideSetting
                {
                    Mode = ResourceOverrideMode.Custom,
                    CustomValue = "#FFAA0000"
                };

                PlayAchResourceService.Apply(resources, overrides, null, LookupIn(Parse(NovaXLikeTheme)));

                AssertColor("#FFAA0000", resources["PlayAch.Brush.Input.Border"]);
                AssertColor("#FFAA0000", resources["PlayAch.Brush.Button.Border"]);
                AssertColor("#FFAA0000", resources["PlayAch.Brush.ScrollBar.Thumb"]);
            });
        }

        [TestMethod]
        public void Apply_FallsBackToControlBorderWhenTheThemeHasNoStyle()
        {
            StaTestHost.Run(() =>
            {
                var resources = new ResourceDictionary();
                var lookup = LookupIn(Parse(XamlHeader + @"
                    <SolidColorBrush x:Key=""TextBrush"" Color=""#FFF2F2F2""/>
                    <SolidColorBrush x:Key=""NormalBrush"" Color=""#FF2C3A67""/>
                </ResourceDictionary>"));

                PlayAchResourceService.Apply(resources, NoOverrides(), null, lookup);

                AssertColor("#FF2C3A67", resources["PlayAch.Brush.Input.Border"]);
                AssertColor("#FF2C3A67", resources["PlayAch.Brush.Button.Border"]);
                AssertColor("#FF2C3A67", resources["PlayAch.Brush.ScrollBar.Thumb"]);
                AssertColor("#FFF2F2F2", resources["PlayAch.Brush.Text"]);
            });
        }

        [TestMethod]
        public void Separator_ReadsTheSeparatorStyleAndFollowsABorderOverride()
        {
            StaTestHost.Run(() =>
            {
                // NovaX: NormalBorderBrush is transparent, its separators use PopupSeparatorBrush.
                var lookup = LookupIn(Parse(XamlHeader + @"
                    <SolidColorBrush x:Key=""NormalBorderBrush"" Color=""Transparent""/>
                    <SolidColorBrush x:Key=""PopupSeparatorBrush"" Color=""#FF5F6062""/>
                    <Style TargetType=""Separator"">
                        <Setter Property=""Background"" Value=""{DynamicResource PopupSeparatorBrush}""/>
                    </Style>
                </ResourceDictionary>"));

                var followed = new ResourceDictionary();
                PlayAchResourceService.Apply(followed, NoOverrides(), null, lookup);

                var overridden = new ResourceDictionary();
                var overrides = NoOverrides();
                overrides["PlayAch.Brush.Border"] = new ResourceOverrideSetting
                {
                    Mode = ResourceOverrideMode.Custom,
                    CustomValue = "#FF00AA00"
                };
                PlayAchResourceService.Apply(overridden, overrides, null, lookup);

                AssertColor("#FF5F6062", followed["PlayAch.Brush.Separator"]);
                AssertColor("#FF00AA00", overridden["PlayAch.Brush.Separator"]);
            });
        }

        [TestMethod]
        public void Text_ReadsTheTextBlockStyleBeforeTextBrush()
        {
            StaTestHost.Run(() =>
            {
                var lookup = LookupIn(Parse(XamlHeader + @"
                    <SolidColorBrush x:Key=""TextBrush"" Color=""#FF000000""/>
                    <SolidColorBrush x:Key=""LabelBrush"" Color=""#FFEEEEEE""/>
                    <Style TargetType=""TextBlock"">
                        <Setter Property=""Foreground"" Value=""{DynamicResource LabelBrush}""/>
                    </Style>
                </ResourceDictionary>"));
                var resources = new ResourceDictionary();

                PlayAchResourceService.Apply(resources, NoOverrides(), null, lookup);

                AssertColor("#FFEEEEEE", resources["PlayAch.Brush.Text"]);
                AssertColor("#FFEEEEEE", PlayAchResourceService.ResolveThemeValue("PlayAch.Brush.Text", lookup));
            });
        }

        private static ResourceDictionary Parse(string xaml)
        {
            return (ResourceDictionary)XamlReader.Parse(xaml);
        }

        private static Func<object, object> LookupIn(ResourceDictionary theme)
        {
            return key => theme.Contains(key) ? theme[key] : null;
        }

        private static Dictionary<string, ResourceOverrideSetting> NoOverrides()
        {
            return new Dictionary<string, ResourceOverrideSetting>(StringComparer.OrdinalIgnoreCase);
        }

        private static void AssertColor(string expected, object value)
        {
            Assert.IsInstanceOfType(value, typeof(SolidColorBrush));
            Assert.AreEqual((Color)ColorConverter.ConvertFromString(expected), ((SolidColorBrush)value).Color);
        }
    }
}
