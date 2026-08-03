using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Images;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Services.Notifications
{
    /// <summary>
    /// A named notification appearance preset on disk. The file is a standard
    /// <c>.pastyle.zip</c> package; the surface it captures is encoded by the folder it lives
    /// in and the display name is the file name minus the package extension.
    /// </summary>
    public sealed class NotificationStylePresetInfo
    {
        public NotificationStylePresetInfo(string name, string filePath, bool isFrame)
        {
            Name = name;
            FilePath = filePath;
            IsFrame = isFrame;
        }

        public string Name { get; }

        public string FilePath { get; }

        public bool IsFrame { get; }

        public override string ToString() => Name;
    }

    /// <summary>
    /// Stores named per-surface appearance presets as self-contained <c>.pastyle.zip</c>
    /// packages under <c>notification_style_presets\toast</c> and <c>...\frame</c> in the
    /// plugin's user data folder. Each preset carries one surface's style plus its bundled
    /// images and optional custom template; packaging and image re-materialization are
    /// delegated to <see cref="NotificationStylePortableStore"/>, so a preset file is also a
    /// valid style package for the regular import/export flow.
    /// </summary>
    public sealed class NotificationStylePresetStore
    {
        public const int MaxPresetCount = 50;
        public const int MaxNameLength = 64;

        private const string PresetsFolderName = "notification_style_presets";
        private const string ToastFolderName = "toast";
        private const string FrameFolderName = "frame";

        private readonly NotificationStylePortableStore _portableStore;
        private readonly string _presetsRoot;

        public NotificationStylePresetStore(
            NotificationStylePortableStore portableStore,
            string pluginUserDataPath)
        {
            _portableStore = portableStore ?? throw new ArgumentNullException(nameof(portableStore));
            if (string.IsNullOrWhiteSpace(pluginUserDataPath))
            {
                throw new ArgumentException("Plugin user data path is required.", nameof(pluginUserDataPath));
            }

            _presetsRoot = Path.Combine(pluginUserDataPath, PresetsFolderName);
        }

        public IReadOnlyList<NotificationStylePresetInfo> ListPresets(bool isFrame)
        {
            var directory = GetSurfaceDirectory(isFrame);
            if (!Directory.Exists(directory))
            {
                return Array.Empty<NotificationStylePresetInfo>();
            }

            return Directory.EnumerateFiles(directory)
                .Where(NotificationStylePortableStore.IsPackagePath)
                .Select(path => new NotificationStylePresetInfo(GetPresetName(path), path, isFrame))
                .OrderBy(preset => preset.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public bool PresetExists(bool isFrame, string name)
        {
            var sanitized = SanitizeName(name);
            return !string.IsNullOrEmpty(sanitized) && File.Exists(GetPresetPath(isFrame, sanitized));
        }

        public int CountPresets(bool isFrame)
        {
            return ListPresets(isFrame).Count;
        }

        /// <summary>
        /// Trims the name, strips characters that cannot appear in a file name, and caps the
        /// length at <see cref="MaxNameLength"/>. Returns an empty string when nothing valid
        /// remains; callers treat that as an invalid name.
        /// </summary>
        public static string SanitizeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            var invalid = Path.GetInvalidFileNameChars();
            var cleaned = new string(name.Trim().Where(c => !invalid.Contains(c)).ToArray()).Trim();
            if (cleaned.Length > MaxNameLength)
            {
                cleaned = cleaned.Substring(0, MaxNameLength).Trim();
            }

            return cleaned;
        }

        /// <summary>
        /// Saves the given surface of <paramref name="currentStyle"/> as a named preset,
        /// overwriting any preset with the same name. Only the saved surface travels, with its
        /// own badge images and header texts riding along inside the surface style; a toast
        /// preset additionally carries the toast-only background image. The other surface is
        /// left at factory defaults in the package and is ignored on apply.
        /// </summary>
        public void SavePreset(
            bool isFrame,
            string name,
            NotificationStyleSettings currentStyle,
            string templateXamlOrNull)
        {
            if (currentStyle == null)
            {
                throw new ArgumentNullException(nameof(currentStyle));
            }

            var sanitized = SanitizeName(name);
            if (string.IsNullOrEmpty(sanitized))
            {
                throw new ArgumentException("Preset name is invalid.", nameof(name));
            }

            _portableStore.ExportSurfacePackage(
                isFrame,
                currentStyle,
                GetPresetPath(isFrame, sanitized),
                templateXamlOrNull);
        }

        /// <summary>
        /// Loads a preset's style, re-materializing any bundled images into managed storage
        /// for <paramref name="targetOwner"/>. The caller merges only the preset's surface
        /// into the target style.
        /// </summary>
        public Task<NotificationStyleSettings> LoadPresetStyleAsync(
            NotificationStylePresetInfo preset,
            NotificationImageOwner targetOwner,
            CancellationToken cancel)
        {
            if (preset == null)
            {
                throw new ArgumentNullException(nameof(preset));
            }

            return _portableStore.ImportAsync(preset.FilePath, targetOwner, cancel);
        }

        /// <summary>
        /// Reads the preset's embedded template XAML for its surface, or null when the preset
        /// was saved without a custom template.
        /// </summary>
        public string ReadPresetTemplateXaml(NotificationStylePresetInfo preset)
        {
            if (preset == null)
            {
                throw new ArgumentNullException(nameof(preset));
            }

            return _portableStore.ReadTemplateXaml(preset.FilePath, preset.IsFrame);
        }

        public void DeletePreset(NotificationStylePresetInfo preset)
        {
            if (preset == null)
            {
                throw new ArgumentNullException(nameof(preset));
            }

            if (File.Exists(preset.FilePath))
            {
                File.Delete(preset.FilePath);
            }
        }

        private string GetSurfaceDirectory(bool isFrame)
        {
            return Path.Combine(_presetsRoot, isFrame ? FrameFolderName : ToastFolderName);
        }

        private string GetPresetPath(bool isFrame, string sanitizedName)
        {
            return Path.Combine(
                GetSurfaceDirectory(isFrame),
                sanitizedName + NotificationStylePortableStore.PackageFileExtension);
        }

        private static string GetPresetName(string filePath)
        {
            var fileName = Path.GetFileName(filePath) ?? string.Empty;
            return fileName.Substring(
                0,
                fileName.Length - NotificationStylePortableStore.PackageFileExtension.Length);
        }
    }
}
