using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Showcase;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PlayniteAchievements.Services.Workshop.Preview
{
    /// <summary>
    /// Reads a downloaded Workshop package into the preview model of its kind. Each model gets a
    /// scratch directory of its own for the files it extracts; when reading fails the directory is
    /// deleted before the exception propagates. Reads files and does CPU work only, so it is safe
    /// to run off the UI thread.
    /// </summary>
    public static class WorkshopPreviewModelBuilder
    {
        /// <summary>The temp folder label preview scratch directories are created under.</summary>
        public const string ScratchFolderLabel = "WorkshopPreview";

        /// <summary>Reads <paramref name="packagePath"/> as a package of <paramref name="kind"/>.</summary>
        public static WorkshopPreviewModel Build(WorkshopItemKind kind, string packagePath, WorkshopPreviewContext context)
        {
            if (string.IsNullOrWhiteSpace(packagePath))
            {
                throw new ArgumentException("Package path is required.", nameof(packagePath));
            }

            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            var scratch = PortablePackage.CreateScratchDirectory(ScratchFolderLabel);
            try
            {
                switch (kind)
                {
                    case WorkshopItemKind.Colors:
                        return BuildColors(packagePath, scratch, context);
                    case WorkshopItemKind.NotificationStyle:
                        return BuildStyle(packagePath, scratch, isFrame: false, context);
                    case WorkshopItemKind.ScreenshotFrame:
                        return BuildStyle(packagePath, scratch, isFrame: true, context);
                    case WorkshopItemKind.UnlockSounds:
                        return BuildSounds(packagePath, scratch, context);
                    case WorkshopItemKind.Bundle:
                        return BuildBundle(packagePath, scratch, context);
                    case WorkshopItemKind.ShowcasePage:
                        return BuildShowcasePage(packagePath, scratch);
                    case WorkshopItemKind.GameCustomData:
                        return BuildGameCustomData(packagePath, scratch, context);
                    default:
                        throw new InvalidOperationException($"Unknown Workshop item kind '{kind}'.");
                }
            }
            catch
            {
                PortablePackage.TryDeleteDirectory(scratch);
                throw;
            }
        }

        private static ColorsPreviewModel BuildColors(string packagePath, string scratch, WorkshopPreviewContext context)
        {
            var store = Require(context.ColorPackPortableStore, "Color set store");
            return new ColorsPreviewModel(packagePath, scratch, store.Read(packagePath));
        }

        private static NotificationStylePreviewModel BuildStyle(
            string packagePath,
            string scratch,
            bool isFrame,
            WorkshopPreviewContext context)
        {
            var store = Require(context.NotificationStylePortableStore, "Notification style store");
            return new NotificationStylePreviewModel(packagePath, scratch, isFrame, store.ReadForPreview(packagePath, scratch));
        }

        private static UnlockSoundsPreviewModel BuildSounds(string packagePath, string scratch, WorkshopPreviewContext context)
        {
            var store = Require(context.UnlockSoundPortableStore, "Unlock sound store");
            var extracted = store.ExtractForPreview(packagePath, scratch);
            var slots = Enum.GetValues(typeof(UnlockSoundTier))
                .Cast<UnlockSoundTier>()
                .Where(tier => extracted.ContainsKey(tier))
                .Select(tier => new UnlockSoundPreviewSlot(tier, extracted[tier], new FileInfo(extracted[tier]).Length))
                .ToList();
            return new UnlockSoundsPreviewModel(packagePath, scratch, slots);
        }

        private static BundlePreviewModel BuildBundle(string packagePath, string scratch, WorkshopPreviewContext context)
        {
            var store = Require(context.BundlePortableStore, "Bundle store");
            var parts = store.Inspect(packagePath);
            var extracted = store.ExtractParts(packagePath, parts, Path.Combine(scratch, "parts"));

            // Each child lives in a sub-folder of the bundle's scratch, so disposing the bundle
            // removes everything even when a child was never disposed on its own.
            var children = new List<WorkshopPreviewModel>();
            try
            {
                T Child<T>(BundleParts part, Func<string, string, T> build) where T : WorkshopPreviewModel
                {
                    if (!extracted.TryGetValue(part, out var partPath))
                    {
                        return null;
                    }

                    var childScratch = Path.Combine(scratch, part.ToString().ToLowerInvariant());
                    Directory.CreateDirectory(childScratch);
                    var child = build(partPath, childScratch);
                    children.Add(child);
                    return child;
                }

                var colors = Child(BundleParts.Colors, (path, dir) => BuildColors(path, dir, context));
                var sounds = Child(BundleParts.Sounds, (path, dir) => BuildSounds(path, dir, context));
                var toast = Child(BundleParts.Toast, (path, dir) => BuildStyle(path, dir, isFrame: false, context));
                var frame = Child(BundleParts.Frame, (path, dir) => BuildStyle(path, dir, isFrame: true, context));
                return new BundlePreviewModel(packagePath, scratch, parts, colors, sounds, toast, frame);
            }
            catch
            {
                foreach (var child in children)
                {
                    child.Dispose();
                }

                throw;
            }
        }

        private static ShowcasePagePreviewModel BuildShowcasePage(string packagePath, string scratch)
        {
            var portable = ShowcasePagePortableStore.Read(packagePath);
            try
            {
                var page = portable.Page;
                var widgets = new Dictionary<string, ShowcaseWidgetInstanceSettings>(StringComparer.OrdinalIgnoreCase);
                foreach (var widget in portable.Widgets ?? new List<ShowcaseWidgetInstanceSettings>())
                {
                    var id = widget?.InstanceId?.Trim();
                    if (!string.IsNullOrEmpty(id) && !widgets.ContainsKey(id))
                    {
                        widgets[id] = widget;
                    }
                }

                // A block without a widget in the file stays as an empty cell, as the page shows it.
                var blocks = new List<ShowcaseBlockPreview>();
                foreach (var block in page.Blocks ?? new List<ShowcaseBlockSettings>())
                {
                    if (block == null)
                    {
                        continue;
                    }

                    var id = block.WidgetInstanceId?.Trim();
                    ShowcaseWidgetInstanceSettings widget = null;
                    if (!string.IsNullOrEmpty(id))
                    {
                        widgets.TryGetValue(id, out widget);
                    }

                    var title = widget?.CustomTitle?.Trim();
                    blocks.Add(new ShowcaseBlockPreview(
                        block.Row,
                        block.Column,
                        block.RowSpan,
                        block.ColumnSpan,
                        widget?.Kind,
                        string.IsNullOrEmpty(title) ? null : title));
                }

                // An unset count falls back as ShowcaseLayoutService.Normalize resolves it.
                var legacySize = page.GridSize ?? ShowcaseLayoutService.DefaultTrackCount;
                var rows = ShowcaseLayoutService.NormalizeTrackCount(page.RowCount > 0 ? page.RowCount : legacySize);
                var columns = ShowcaseLayoutService.NormalizeTrackCount(page.ColumnCount > 0 ? page.ColumnCount : legacySize);
                var images = (portable.BundledImages ?? new Dictionary<string, string>())
                    .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(pair => pair.Value)
                    .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    .ToList();

                return new ShowcasePagePreviewModel(
                    packagePath,
                    scratch,
                    portable,
                    page.Name,
                    rows,
                    columns,
                    ShowcaseLayoutService.NormalizeTrackWeights(page.RowWeights, rows),
                    ShowcaseLayoutService.NormalizeTrackWeights(page.ColumnWeights, columns),
                    blocks,
                    images);
            }
            catch
            {
                ShowcasePagePortableStore.DeleteExtractedImages(portable);
                throw;
            }
        }

        private static GameCustomDataPreviewModel BuildGameCustomData(string packagePath, string scratch, WorkshopPreviewContext context)
        {
            var store = Require(context.GameCustomDataStore, "Game custom data store");
            var package = store.ReadPortablePackage(packagePath, scratch);
            var diff = GameCustomDataPreviewDiffBuilder.Build(package, context.GameDataSource);
            return new GameCustomDataPreviewModel(packagePath, scratch, package, diff);
        }

        private static T Require<T>(T store, string name) where T : class
        {
            return store ?? throw new InvalidOperationException(name + " is not available.");
        }
    }
}
