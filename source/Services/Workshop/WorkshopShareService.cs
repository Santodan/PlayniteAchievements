using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Showcase;
using PlayniteAchievements.Services.UI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Services.Workshop
{
    /// <summary>Something this install can share: the global look pieces, a showcase page, or a game's data.</summary>
    public sealed class WorkshopShareCandidate
    {
        public WorkshopItemKind Kind { get; set; }

        /// <summary>Shown in the share list.</summary>
        public string Label { get; set; }

        /// <summary>Proposed item name.</summary>
        public string DefaultName { get; set; }

        public Guid? GameId { get; set; }

        public string PageId { get; set; }

        /// <summary>
        /// For a bundle, the standalone package per part the composer chose (current settings
        /// exported to scratch, or a preset file). Null means every part from the live settings.
        /// </summary>
        public IReadOnlyDictionary<BundleParts, string> BundlePartFiles { get; set; }

        /// <summary>
        /// A ready-made package to share as it is (a saved preset file). When set, nothing is
        /// exported from the live settings; the file is copied under the item name.
        /// </summary>
        public string PackagePath { get; set; }

        /// <summary>The library item shared, when shared from the Library; recorded with the submission.</summary>
        public string LibraryItemId { get; set; }

        /// <summary>The Workshop id this install already published the candidate as; the dialog offers updating it first.</summary>
        public string PublishedItemId { get; set; }
    }

    public enum WorkshopSharePhase
    {
        Packaging,
        Uploading,
        Submitting
    }

    public sealed class WorkshopShareProgress
    {
        public WorkshopSharePhase Phase { get; set; }
        public long BytesSent { get; set; }
        public long BytesTotal { get; set; }
    }

    /// <summary>
    /// Builds the package for something the user wants to share, using the same export code the
    /// settings pages use (so personal progress is stripped the same way), uploads it through the
    /// submission service, and records the submission so its progress can be followed.
    /// </summary>
    public sealed class WorkshopShareService
    {
        private readonly PlayniteAchievementsPlugin _plugin;
        private readonly WorkshopSubmissionClient _client;
        private readonly WorkshopIdentityStore _identity;
        private readonly ILogger _logger;

        public WorkshopShareService(
            PlayniteAchievementsPlugin plugin,
            WorkshopSubmissionClient client,
            WorkshopIdentityStore identity,
            ILogger logger = null)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _identity = identity ?? throw new ArgumentNullException(nameof(identity));
            _logger = logger;
        }

        public IReadOnlyList<WorkshopShareCandidate> ListCandidates()
        {
            var persisted = _plugin.Settings?.Persisted;
            var result = new List<WorkshopShareCandidate>();
            if (persisted == null)
            {
                return result;
            }

            result.Add(new WorkshopShareCandidate
            {
                Kind = WorkshopItemKind.Colors,
                Label = ResourceProvider.GetString("LOCPlayAch_Settings_Display_Colors"),
                DefaultName = ResourceProvider.GetString("LOCPlayAch_Settings_Display_Colors")
            });
            result.Add(new WorkshopShareCandidate
            {
                Kind = WorkshopItemKind.NotificationStyle,
                Label = ResourceProvider.GetString("LOCPlayAch_Settings_Style_ToastTab"),
                DefaultName = ResourceProvider.GetString("LOCPlayAch_Settings_Style_ToastTab")
            });
            result.Add(new WorkshopShareCandidate
            {
                Kind = WorkshopItemKind.ScreenshotFrame,
                Label = ResourceProvider.GetString("LOCPlayAch_Settings_FrameHeader"),
                DefaultName = ResourceProvider.GetString("LOCPlayAch_Settings_FrameHeader")
            });

            var resolved = _plugin.UnlockSounds?.Resolver?.ResolveAll();
            if (resolved != null && resolved.Any(s => s.Source == UnlockSoundSource.Custom || s.Source == UnlockSoundSource.Theme))
            {
                result.Add(new WorkshopShareCandidate
                {
                    Kind = WorkshopItemKind.UnlockSounds,
                    Label = ResourceProvider.GetString("LOCPlayAch_Workshop_Share_Sounds"),
                    DefaultName = ResourceProvider.GetString("LOCPlayAch_Workshop_Share_Sounds")
                });
            }

            result.Add(new WorkshopShareCandidate
            {
                Kind = WorkshopItemKind.Bundle,
                Label = ResourceProvider.GetString("LOCPlayAch_Workshop_Share_Bundle"),
                DefaultName = ResourceProvider.GetString("LOCPlayAch_Workshop_Kind_Bundle")
            });

            foreach (var page in persisted.Showcase?.Pages ?? new List<ShowcasePageSettings>())
            {
                if (page == null || string.IsNullOrWhiteSpace(page.PageId))
                {
                    continue;
                }

                result.Add(new WorkshopShareCandidate
                {
                    Kind = WorkshopItemKind.ShowcasePage,
                    Label = string.Format(ResourceProvider.GetString("LOCPlayAch_Workshop_Share_ShowcasePage"), page.Name),
                    DefaultName = page.Name,
                    PageId = page.PageId
                });
            }

            var store = _plugin.GameCustomDataStore;
            if (store != null)
            {
                foreach (var data in store.LoadAll())
                {
                    if (data == null || data.PlayniteGameId == Guid.Empty || !store.HasPortableData(data.PlayniteGameId))
                    {
                        continue;
                    }

                    var name = _plugin.PlayniteApi?.Database?.Games?.Get(data.PlayniteGameId)?.Name
                               ?? _plugin.AchievementDataService?.GetGameHeader(data.PlayniteGameId)?.GameName;
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    result.Add(new WorkshopShareCandidate
                    {
                        Kind = WorkshopItemKind.GameCustomData,
                        Label = string.Format(ResourceProvider.GetString("LOCPlayAch_Workshop_Share_GameData"), name),
                        DefaultName = name,
                        GameId = data.PlayniteGameId
                    });
                }
            }

            return result;
        }

        /// <summary>Writes the candidate's package into <paramref name="directory"/> and returns its path.</summary>
        public string BuildPackage(WorkshopShareCandidate candidate, string directory)
        {
            if (candidate == null)
            {
                throw new ArgumentNullException(nameof(candidate));
            }

            var persisted = _plugin.Settings?.Persisted ?? throw new InvalidOperationException("Settings are not available.");
            Directory.CreateDirectory(directory);
            var stem = SafeStem(candidate.DefaultName);

            if (!string.IsNullOrWhiteSpace(candidate.PackagePath))
            {
                if (!File.Exists(candidate.PackagePath))
                {
                    throw new FileNotFoundException("The preset file is missing.", candidate.PackagePath);
                }

                var copy = Path.Combine(directory, stem + Path.GetExtension(candidate.PackagePath));
                File.Copy(candidate.PackagePath, copy, overwrite: true);
                return copy;
            }

            var resolver = CreateTemplateResolver();

            switch (candidate.Kind)
            {
                case WorkshopItemKind.Colors:
                {
                    var path = Path.Combine(directory, stem + ColorPackPortableStore.PackageFileExtension);
                    _plugin.ColorPackPortableStore.Export(persisted, path);
                    return path;
                }

                case WorkshopItemKind.NotificationStyle:
                {
                    var path = Path.Combine(directory, stem + Notifications.NotificationStylePortableStore.ToastPackageFileExtension);
                    _plugin.NotificationStylePortableStore.ExportSurfacePackage(
                        isFrame: false,
                        persisted.NotificationStyle,
                        path,
                        resolver.ReadCustomTemplateXaml(isFrame: false, providerKey: null, gameId: Guid.Empty));
                    return path;
                }

                case WorkshopItemKind.ScreenshotFrame:
                {
                    var path = Path.Combine(directory, stem + Notifications.NotificationStylePortableStore.FramePackageFileExtension);
                    _plugin.NotificationStylePortableStore.ExportSurfacePackage(
                        isFrame: true,
                        persisted.NotificationStyle,
                        path,
                        resolver.ReadCustomTemplateXaml(isFrame: true, providerKey: null, gameId: Guid.Empty));
                    return path;
                }

                case WorkshopItemKind.UnlockSounds:
                {
                    var path = Path.Combine(directory, stem + Sound.UnlockSoundPortableStore.PackageFileExtension);
                    _plugin.UnlockSoundPortableStore.Export(_plugin.UnlockSounds?.Resolver?.ResolveAll() ?? Array.Empty<ResolvedUnlockSound>(), path);
                    return path;
                }

                case WorkshopItemKind.Bundle:
                {
                    var path = Path.Combine(directory, stem + BundlePortableStore.PackageFileExtension);
                    if (candidate.BundlePartFiles != null && candidate.BundlePartFiles.Count > 0)
                    {
                        _plugin.BundlePortableStore.ExportParts(path, candidate.BundlePartFiles);
                        return path;
                    }

                    _plugin.BundlePortableStore.Export(
                        path,
                        BundleParts.All,
                        persisted,
                        _plugin.UnlockSounds?.Resolver?.ResolveAll(),
                        resolver.ReadCustomTemplateXaml(isFrame: false, providerKey: null, gameId: Guid.Empty),
                        resolver.ReadCustomTemplateXaml(isFrame: true, providerKey: null, gameId: Guid.Empty));
                    return path;
                }

                case WorkshopItemKind.ShowcasePage:
                {
                    var portable = ShowcasePagePortableStore.BuildPortable(persisted.Showcase, persisted.GridOptions, candidate.PageId)
                                   ?? throw new InvalidOperationException("The showcase page no longer exists.");
                    var path = Path.Combine(directory, stem + ShowcasePagePortableStore.PackageFileExtension);
                    ShowcasePagePortableStore.Write(path, portable);
                    return path;
                }

                case WorkshopItemKind.GameCustomData:
                {
                    var gameId = candidate.GameId ?? throw new InvalidOperationException("No game selected.");
                    var path = Path.Combine(directory, stem + GameCustomData.GameCustomDataStore.PortableFileExtension);
                    _plugin.GameCustomDataStore.ExportPortablePackage(gameId, path);
                    return path;
                }

                default:
                    throw new InvalidOperationException($"Cannot share a {candidate.Kind}.");
            }
        }

        /// <summary>Asks the Workshop to take down an item this install published.</summary>
        public Task<WorkshopSubmissionReceipt> RemoveAsync(WorkshopItemKind kind, string itemId, CancellationToken cancel)
        {
            if (string.IsNullOrWhiteSpace(itemId))
            {
                throw new ArgumentException("No Workshop item id.", nameof(itemId));
            }

            var submission = new WorkshopSubmission { Remove = true, ExistingId = itemId, Author = _identity.DisplayName };
            return ShareAsync(new WorkshopShareCandidate { Kind = kind }, submission, null, null, cancel);
        }

        /// <summary>
        /// Packages, uploads, and submits. Returns the service's receipt and records it locally.
        /// <paramref name="prebuiltPackagePath"/>, when it names an existing file, is uploaded as
        /// the package instead of building the candidate again (the share dialog builds one for
        /// its preview image). <paramref name="coverPath"/>, when it names an existing file, is
        /// uploaded as the item's optional cover image.
        /// </summary>
        public async Task<WorkshopSubmissionReceipt> ShareAsync(
            WorkshopShareCandidate candidate,
            WorkshopSubmission submission,
            string previewPath,
            IProgress<WorkshopShareProgress> progress,
            CancellationToken cancel,
            string prebuiltPackagePath = null,
            string coverPath = null)
        {
            if (!_client.IsConfigured)
            {
                throw new InvalidOperationException("No Workshop submission service is configured.");
            }

            var work = Path.Combine(Path.GetTempPath(), "PlayniteAchievements", "WorkshopShare", Guid.NewGuid().ToString("N"));
            try
            {
                string packageKey = null;
                if (!submission.Remove)
                {
                    progress?.Report(new WorkshopShareProgress { Phase = WorkshopSharePhase.Packaging });
                    var packagePath = !string.IsNullOrWhiteSpace(prebuiltPackagePath) && File.Exists(prebuiltPackagePath)
                        ? prebuiltPackagePath
                        : BuildPackage(candidate, work);
                    var total = new FileInfo(packagePath).Length;
                    progress?.Report(new WorkshopShareProgress { Phase = WorkshopSharePhase.Uploading, BytesTotal = total });
                    packageKey = await _client.UploadAsync(
                        packagePath,
                        "application/zip",
                        new Progress<long>(sent => progress?.Report(new WorkshopShareProgress { Phase = WorkshopSharePhase.Uploading, BytesSent = sent, BytesTotal = total })),
                        cancel).ConfigureAwait(false);
                }

                string previewKey = null;
                if (!submission.Remove && !string.IsNullOrWhiteSpace(previewPath) && File.Exists(previewPath))
                {
                    previewKey = await _client.UploadAsync(previewPath, ContentTypeFor(previewPath), null, cancel).ConfigureAwait(false);
                }

                string coverKey = null;
                if (!submission.Remove && !string.IsNullOrWhiteSpace(coverPath) && File.Exists(coverPath))
                {
                    coverKey = await _client.UploadAsync(coverPath, ContentTypeFor(coverPath), null, cancel).ConfigureAwait(false);
                }

                progress?.Report(new WorkshopShareProgress { Phase = WorkshopSharePhase.Submitting });
                submission.Kind = candidate.Kind;
                return await SubmitAndRecordAsync(
                    submission,
                    packageKey,
                    previewKey,
                    coverKey,
                    submission.Remove ? null : candidate.LibraryItemId,
                    cancel).ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    if (Directory.Exists(work))
                    {
                        Directory.Delete(work, recursive: true);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, "Failed cleaning the Workshop share scratch folder.");
                }
            }
        }

        /// <summary>
        /// Changes the details of an item this install published without sending a package:
        /// the Workshop keeps its package, version and preview. <paramref name="coverPath"/>,
        /// when it names an existing file, replaces the cover; otherwise the cover stays.
        /// </summary>
        public async Task<WorkshopSubmissionReceipt> UpdateMetadataAsync(
            WorkshopItem item,
            WorkshopSubmission submission,
            string coverPath,
            CancellationToken cancel)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.Id))
            {
                throw new ArgumentException("No Workshop item.", nameof(item));
            }

            if (!_client.IsConfigured)
            {
                throw new InvalidOperationException("No Workshop submission service is configured.");
            }

            string coverKey = null;
            if (!string.IsNullOrWhiteSpace(coverPath) && File.Exists(coverPath))
            {
                coverKey = await _client.UploadAsync(coverPath, ContentTypeFor(coverPath), null, cancel).ConfigureAwait(false);
            }

            submission.Kind = item.Kind;
            submission.ExistingId = item.Id;
            submission.Remove = false;
            return await SubmitAndRecordAsync(submission, null, null, coverKey, null, cancel).ConfigureAwait(false);
        }

        /// <summary>Submits and records the submission locally so its progress can be followed.</summary>
        private async Task<WorkshopSubmissionReceipt> SubmitAndRecordAsync(
            WorkshopSubmission submission,
            string packageKey,
            string previewKey,
            string coverKey,
            string libraryItemId,
            CancellationToken cancel)
        {
            var receipt = await _client.SubmitAsync(
                submission,
                _identity.GetOrCreateSubmitterKey(),
                packageKey,
                previewKey,
                PluginManifest.Version,
                cancel,
                coverKey).ConfigureAwait(false);

            _identity.DisplayName = submission.Author;
            _identity.RecordSubmission(new WorkshopSubmissionRecord
            {
                IssueNumber = receipt.IssueNumber,
                IssueUrl = receipt.IssueUrl,
                Name = submission.Remove ? submission.ExistingId : submission.Name,
                Kind = submission.Kind,
                ItemId = submission.ExistingId,
                SubmittedUtc = DateTime.UtcNow,
                LastState = "validating",
                LibraryItemId = libraryItemId
            });
            return receipt;
        }

        private AchievementToastTemplateResolver CreateTemplateResolver()
        {
            return new AchievementToastTemplateResolver(
                _plugin.PlayniteApi,
                _logger,
                customTemplatesDirectory: AchievementToastTemplateResolver.GetCustomTemplatesDirectory(
                    _plugin.GetPluginUserDataPath()));
        }

        private static string SafeStem(string name)
        {
            var invalid = new HashSet<char>(Path.GetInvalidFileNameChars());
            var stem = new string((name ?? string.Empty).Where(c => !invalid.Contains(c)).ToArray()).Trim().TrimEnd('.');
            return stem.Length == 0 ? "package" : stem;
        }

        private static string ContentTypeFor(string path)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".png": return "image/png";
                case ".jpg":
                case ".jpeg": return "image/jpeg";
                case ".gif": return "image/gif";
                case ".webp": return "image/webp";
                default: return "application/octet-stream";
            }
        }
    }
}
