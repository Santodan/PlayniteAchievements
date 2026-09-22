using System.Windows;

// Lets this assembly's own custom controls (Views/Controls/SettingRow and any future ones) carry a
// default template in Themes/Generic.xaml. An implicit style in a merged dictionary is not enough:
// a Control subclass resolves its default template from the theme dictionary, so without this the
// control renders as nothing at all. None/SourceAssembly means no per-OS-theme dictionaries, just
// the one Generic.xaml in this assembly, so nothing here affects Playnite's own theming.
[assembly: ThemeInfo(ResourceDictionaryLocation.None, ResourceDictionaryLocation.SourceAssembly)]
