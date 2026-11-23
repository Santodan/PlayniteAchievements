using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using PlayniteAchievements.Providers.Settings;
using Microsoft.Win32;
using PlayniteAchievements.Views.Converters;

namespace PlayniteAchievements.Providers.Local
{
    public sealed class NativeSourcesSettingsView : ProviderSettingsViewBase
    {
        private readonly string _sourceKey;
        public NativeSourcesSettingsView(string sourceKey = null) { _sourceKey = sourceKey; }

        public override void Initialize(IProviderSettings settings)
        {
            base.Initialize(settings);
            var local = (LocalSettings)settings;
            var panel = new StackPanel { Margin = new Thickness(12) };
            AddText(panel, NativeSourceCatalog.Find(_sourceKey)?.Name ?? "Native source folders", true);
            AddText(panel, "These sources use the Local provider. Shared game libraries are configured in Native source folders; each emulator or port has its own menu for locations and profiles. Manage Achievements → Local Overrides → Native Sources can override one game's files. Save applies these settings; Cancel discards edits.");
            if (_sourceKey == null)
            {
                var enabled = new CheckBox { Content = "Enable native source detection", Margin = new Thickness(0, 12, 0, 12) };
                enabled.SetBinding(CheckBox.IsCheckedProperty, new Binding(nameof(LocalSettings.NativeSourcesEnabled)) { Source = local, Mode = BindingMode.TwoWay });
                panel.Children.Add(enabled);
                AddFolderList(panel, "Game-library folders (shared by all native sources)", local.NativeGameLibraryFolders, text => local.NativeGameLibraryFolders = text);
                AddText(panel, "Add folders containing your installed ports and recompilations, one per line. Discovery is limited to these folders, three levels and 400 directories per root. A game must match its Playnite name/title ID; ambiguous matches need a per-game install override.");
                var quiver = new CheckBox { Content = "Discover installed games from Quiver Launcher libraries", IsChecked = local.NativeUseQuiver, Margin = new Thickness(0, 8, 0, 8) };
                quiver.Checked += (_, __) => local.NativeUseQuiver = true;
                quiver.Unchecked += (_, __) => local.NativeUseQuiver = false;
                panel.Children.Add(quiver);
                AddFolderList(panel, "Additional Quiver data folders (optional)", local.NativeQuiverFolders, text => local.NativeQuiverFolders = text);
                AddText(panel, "Select the folder containing apps.json (Quiver 3) or installs.json (Quiver 4). Standard Quiver locations are also checked when discovery is enabled.");
            }
            if (_sourceKey == "x360recomp")
            {
                var download = new CheckBox { Content = "Download missing Xbox 360 recompilation definitions from dbox.tools (cached for offline use)", Margin = new Thickness(0, 8, 0, 12) };
                download.SetBinding(CheckBox.IsCheckedProperty, new Binding(nameof(LocalSettings.DownloadNativeXboxDefinitions)) { Source = local, Mode = BindingMode.TwoWay });
                panel.Children.Add(download);
            }
            foreach (var source in NativeSourceCatalog.Sources.Where(s => s.Key == _sourceKey))
            {
                var check = new CheckBox { Content = "Enable " + source.Name, IsChecked = !local.DisabledNativeSources.Contains(source.Key), Margin = new Thickness(0, 12, 0, 4) };
                check.Checked += (_, __) => local.DisabledNativeSources = local.DisabledNativeSources.Where(k => k != source.Key).ToList();
                check.Unchecked += (_, __) => local.DisabledNativeSources = local.DisabledNativeSources.Concat(new[] { source.Key }).Distinct().ToList();
                panel.Children.Add(check);
                AddIconSelector(panel, source, local);
                AddText(panel, source.Files);
                var locations = local.NativeSourceLocations.TryGetValue(source.Key, out var configured) ? configured : new NativeSourceLocations();
                var details = new StackPanel { Margin = new Thickness(12, 4, 0, 12) };
                Action<Action<NativeSourceLocations>> update = change =>
                {
                    var copy = local.NativeSourceLocations.TryGetValue(source.Key, out var current) ? current.Copy() : new NativeSourceLocations();
                    change(copy);
                    local.NativeSourceLocations = new Dictionary<string, NativeSourceLocations>(local.NativeSourceLocations) { [source.Key] = copy };
                };
                AddFolderList(details, "Game / port install folders or libraries", locations.InstallFolders, text => update(l => l.InstallFolders = text));
                AddFolderList(details, "Achievement save folders (optional replacement for automatic save locations)", locations.SaveFolders, text => update(l => l.SaveFolders = text));
                AddText(details, SaveHint(source.Key));
                if (source.Key == "kytyps5" || source.Key == "ps3recomp" || source.Key == "ps4recomp" || source.Key == "x360recomp" || source.Key == "markerpatch" || source.Key == "madnesspatch" || source.Key == "ladxhd")
                {
                    AddFolderList(details, "Game-dump / achievement-definition folders (optional)", locations.DefinitionFolders, text => update(l => l.DefinitionFolders = text));
                    AddText(details, "Leave blank to read definitions from the game's install. Shared console dump folders must identify the game by its Playnite name or title ID; otherwise select its definition file in Local Overrides.");
                }
                if (source.Key == "kytyps5")
                {
                    AddFolderList(details, "Kyty emulator folders (containing kyty_emulator.exe and _Trophies)", locations.EmulatorFolders, text => update(l => l.EmulatorFolders = text));
                    AddSinglePath(details, "Kyty.ini override (optional)", locations.SettingsFile, text => update(l => l.SettingsFile = text), true);
                    AddText(details, "Kyty.ini is normally read beside the emulator, then from ProgramData/Kyty. Its configured game folders and per-game/global user IDs are followed. You can select _Trophies or one title's trophy folder instead of the emulator root.");
                    AddEntry(details, "Kyty user ID override (optional; blank follows Kyty.ini)", locations.UserId, text => update(l => l.UserId = text));
                }
                else AddEntry(details, "Default profile / slot (optional; filename without extension or containing folder)", locations.Profile, text => update(l => l.Profile = text));
                var automatic = new CheckBox { Content = "Use standard AppData / Documents save locations when save folders above are empty", IsChecked = locations.UseDefaultLocations, Margin = new Thickness(0, 8, 0, 8) };
                automatic.Checked += (_, __) => update(l => l.UseDefaultLocations = true);
                automatic.Unchecked += (_, __) => update(l => l.UseDefaultLocations = false);
                details.Children.Add(automatic);
                AddText(details, "Folders accept Windows environment variables and the existing per-game variables such as {GameName}, {InstallDir} and {RomDir}. Explicit per-game file overrides take priority. Multiple profiles are never combined.");
                var reset = new Button { Content = "Reset this source's locations", HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 90, Padding = new Thickness(12, 6, 12, 6) };
                reset.Click += (_, __) => { update(l => { l.InstallFolders = l.SaveFolders = l.DefinitionFolders = l.EmulatorFolders = l.SettingsFile = l.UserId = l.Profile = ""; l.UseDefaultLocations = true; }); Initialize(settings); };
                details.Children.Add(reset);
                panel.Children.Add(details);
            }
            AddText(panel, "When several slots, profiles or trophy sets match, choose the exact unlock/definition file per game. Inspection is read-only. Native saves are never edited. A missing or invalid save leaves cached achievements intact. Some ports do not record historical unlock dates.");
            Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = panel };
        }
        private static void AddText(Panel panel, string text, bool heading = false)
        {
            var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8), FontWeight = heading ? FontWeights.SemiBold : FontWeights.Normal };
            block.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            panel.Children.Add(block);
        }

        private sealed class IconPlatformChoice
        {
            public string Key { get; set; }
            public string Name { get; set; }
            public override string ToString() => Name;
        }
        private static void AddIconSelector(Panel panel, NativeSourceOption source, LocalSettings settings)
        {
            AddText(panel, "Menu icon", true);
            AddText(panel, "Default: " + source.OriginalConsoleName + ". Browse for a custom image or borrow another platform's icon. Custom images take priority and are copied as PNG up to 64 × 64 pixels. Save applies the selection; Cancel keeps the saved icon.");
            var preview = new Image { Width = 48, Height = 48, Stretch = System.Windows.Media.Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8) };
            panel.Children.Add(preview);
            var path = FieldBox(""); path.IsReadOnly = true; path.ToolTip = "Managed custom icon path";
            var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8) }; status.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            var browse = RowButton("Browse");
            var clear = RowButton("Remove");
            panel.Children.Add(FieldRow(path, browse, clear));
            Action refresh = () =>
            {
                settings.NativeSourceIcons.TryGetValue(source.Key, out var current);
                path.Text = current?.CustomIconPath ?? "";
                clear.IsEnabled = !string.IsNullOrWhiteSpace(current?.CustomIconPath);
                preview.Source = ProviderIconConverter.BuildIcon(NativeSourceIconService.IconKey(source, settings), NativeSourceIconService.Color(source, settings));
            };
            Action<Action<NativeSourceIconSettings>> update = change =>
            {
                var copy = settings.NativeSourceIcons.TryGetValue(source.Key, out var current) ? current.Copy() : new NativeSourceIconSettings();
                change(copy);
                settings.NativeSourceIcons = new Dictionary<string, NativeSourceIconSettings>(settings.NativeSourceIcons) { [source.Key] = copy };
                refresh();
            };
            browse.Click += async (_, __) =>
            {
                var dialog = new OpenFileDialog { Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|All files|*.*", CheckFileExists = true };
                if (dialog.ShowDialog() != true) return;
                browse.IsEnabled = false;
                try
                {
                    var stored = await NativeSourceIconService.ImportAsync(source.Key, dialog.FileName);
                    if (string.IsNullOrWhiteSpace(stored)) { status.Text = "The image could not be imported."; return; }
                    update(icon => icon.CustomIconPath = stored); status.Text = "Custom icon selected. Save to apply.";
                }
                catch (Exception ex) { status.Text = "The image could not be imported: " + ex.Message; }
                finally { browse.IsEnabled = true; }
            };
            clear.Click += (_, __) => { update(icon => icon.CustomIconPath = ""); status.Text = "Custom image cleared. Save to apply."; };
            AddText(panel, "Or use another platform's icon:");
            var choices = new List<IconPlatformChoice> { new IconPlatformChoice { Key = "", Name = "Default (" + source.OriginalConsoleName + ")" } };
            var registry = ProviderRegistry.Instance;
            if (registry != null)
                foreach (var key in registry.GetSettingsViewProviderKeys())
                    if (registry.TryGetProviderVisuals(key, out var icon, out _) && !string.IsNullOrWhiteSpace(icon))
                        choices.Add(new IconPlatformChoice { Key = key, Name = ProviderRegistry.GetLocalizedName(key) });
            settings.NativeSourceIcons.TryGetValue(source.Key, out var selected);
            var platform = new ComboBox { ItemsSource = choices, SelectedValuePath = "Key", DisplayMemberPath = "Name", SelectedValue = selected?.BorrowedProviderIconKey ?? "", Margin = new Thickness(0, 0, 0, 8) };
            platform.SelectionChanged += (_, __) => { update(icon => icon.BorrowedProviderIconKey = platform.SelectedValue as string ?? ""); status.Text = "Platform icon selected. Custom images take priority."; };
            panel.Children.Add(platform); panel.Children.Add(status); refresh();
        }

        private static void AddFolderList(Panel panel, string label, string value, Action<string> save)
        {
            AddText(panel, label, true);
            var browse = RowButton("Add folder");
            var box = FieldBox(value);
            box.AcceptsReturn = true; box.MinHeight = 55; box.MaxHeight = 140; box.TextWrapping = TextWrapping.Wrap;
            box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; box.VerticalContentAlignment = VerticalAlignment.Top;
            box.TextChanged += (_, __) => save(box.Text);
            browse.Click += (_, __) =>
            {
                using (var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = label, ShowNewFolderButton = false })
                    if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                        box.Text = string.Join(Environment.NewLine, NativeSourceLocations.Split(box.Text).Concat(new[] { dialog.SelectedPath }).Distinct(StringComparer.OrdinalIgnoreCase));
            };
            panel.Children.Add(FieldRow(box, browse));
        }

        // The Local page's path rows: the field fills the width; each button is at least 90 px
        // wide, 8 px apart, and as tall as the field.
        private static Button RowButton(string text) =>
            new Button { Content = text, MinWidth = 90, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(8, 0, 0, 0) };

        private static TextBox FieldBox(string value) =>
            new TextBox { Text = value ?? "", Padding = new Thickness(8, 6, 8, 6), VerticalContentAlignment = VerticalAlignment.Center };

        private static Grid FieldRow(TextBox box, params Button[] buttons)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(box);
            for (var i = 0; i < buttons.Length; i++)
            {
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Grid.SetColumn(buttons[i], i + 1);
                row.Children.Add(buttons[i]);
            }
            return row;
        }
        private static void AddSinglePath(Panel panel, string label, string value, Action<string> save, bool ini)
        {
            AddText(panel, label, true);
            var browse = RowButton("Browse file");
            var box = FieldBox(value); box.TextChanged += (_, __) => save(box.Text);
            browse.Click += (_, __) => { var dialog = new OpenFileDialog { Filter = ini ? "Kyty settings|Kyty.ini|INI files|*.ini|All files|*.*" : "All files|*.*", CheckFileExists = true }; if (dialog.ShowDialog() == true) box.Text = dialog.FileName; };
            panel.Children.Add(FieldRow(box, browse));
        }
        private static void AddEntry(Panel panel, string label, string value, Action<string> save)
        {
            AddText(panel, label, true); var box = FieldBox(value); box.Margin = new Thickness(0, 0, 0, 8);
            box.TextChanged += (_, __) => save(box.Text); panel.Children.Add(box);
        }
        private static string SaveHint(string source)
        {
            switch (source)
            {
                case "kytyps5": return "Use emulator folders below for the usual layout. A separate save folder may be _Trophies or its parent. Only this game's title ID and the selected user's trophy files are considered.";
                case "ps3recomp": return "Choose the relocated gamedata/trophies folder. The NPWR identity from the game's selected TROPHY.TRP is used to find its save.";
                case "ps4recomp": return "Choose folders containing game-named/title-ID subfolders for logs, user snapshots or trophy flags. For an unusual launcher layout, select its exact save file per game.";
                case "x360recomp": return "Choose Documents or a relocated save library with a game-name/title-ID folder for each port. Per-game TOML/TSV/JSON files can also be selected in Local Overrides.";
                case "ghostship": return "Choose Ghostship's saves folder. Set the default slot to save_0, save_1, save_2 or save_3; a per-game choice takes priority.";
                case "madnesspatch": return "Choose the Alice CheckPoint folder (containing profile folders), or the active profile's folder. Set a default profile name to avoid ambiguity.";
                default: return "Choose the folder containing this port's achievement save, or its immediate parent library. The expected filename is listed above. Empty uses the normal portable or AppData/Documents layout.";
            }
        }
    }
}
