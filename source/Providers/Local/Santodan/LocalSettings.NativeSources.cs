using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Providers.Local
{
    public partial class LocalSettings
    {
        private bool _nativeSourcesEnabled = true;
        private bool _downloadNativeXboxDefinitions = true;
        private List<string> _disabledNativeSources = new List<string>();
        private Dictionary<Guid, NativeSourceConfiguration> _nativeSourceOverrides = new Dictionary<Guid, NativeSourceConfiguration>();
        private Dictionary<string, NativeSourceLocations> _nativeSourceLocations = new Dictionary<string, NativeSourceLocations>();
        private Dictionary<string, NativeSourceIconSettings> _nativeSourceIcons = new Dictionary<string, NativeSourceIconSettings>();
        private string _nativeGameLibraryFolders = "", _nativeQuiverFolders = "";
        private bool _nativeUseQuiver = true;
        public bool NativeSourcesEnabled { get => _nativeSourcesEnabled; set => SetValue(ref _nativeSourcesEnabled, value); }
        public bool DownloadNativeXboxDefinitions { get => _downloadNativeXboxDefinitions; set => SetValue(ref _downloadNativeXboxDefinitions, value); }
        public List<string> DisabledNativeSources { get => _disabledNativeSources; set => SetValue(ref _disabledNativeSources, value ?? new List<string>()); }
        public Dictionary<Guid, NativeSourceConfiguration> NativeSourceOverrides { get => _nativeSourceOverrides; set => SetValue(ref _nativeSourceOverrides, value ?? new Dictionary<Guid, NativeSourceConfiguration>()); }
        public Dictionary<string, NativeSourceLocations> NativeSourceLocations { get => _nativeSourceLocations; set => SetValue(ref _nativeSourceLocations, value ?? new Dictionary<string, NativeSourceLocations>()); }
        public Dictionary<string, NativeSourceIconSettings> NativeSourceIcons { get => _nativeSourceIcons; set => SetValue(ref _nativeSourceIcons, value ?? new Dictionary<string, NativeSourceIconSettings>()); }
        public string NativeGameLibraryFolders { get => _nativeGameLibraryFolders; set => SetValue(ref _nativeGameLibraryFolders, value ?? ""); }
        public string NativeQuiverFolders { get => _nativeQuiverFolders; set => SetValue(ref _nativeQuiverFolders, value ?? ""); }
        public bool NativeUseQuiver { get => _nativeUseQuiver; set => SetValue(ref _nativeUseQuiver, value); }
    }
}
