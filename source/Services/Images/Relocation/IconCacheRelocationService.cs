using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;

namespace PlayniteAchievements.Services.Images.Relocation
{
    public enum IconCacheMoveOutcome
    {
        Moved = 0,
        Cancelled,
        RefreshRunning,
        AlreadyMoving,
        NotEnoughSpace,
        Invalid,
        Failed
    }

    public sealed class IconCacheMoveResult
    {
        public IconCacheMoveOutcome Outcome { get; set; }

        public IconCacheMoveProblem Problem { get; set; }

        public Exception Error { get; set; }

        public long RequiredBytes { get; set; }

        public long AvailableBytes { get; set; }
    }

    /// <summary>What a move needs from the rest of the plugin.</summary>
    public interface IIconCacheRelocationHost
    {
        bool TryReserveRefreshes();

        void ReleaseRefreshes();

        /// <summary>
        /// Points every stored absolute path under any of <paramref name="oldRoots"/> at the same
        /// file under <paramref name="newRoot"/>, and records <paramref name="settingValue"/> as the
        /// icon cache folder setting so that a settings Cancel keeps it. Returns true when the
        /// settings were also saved to disk.
        /// </summary>
        bool CommitStoredPaths(IReadOnlyList<string> oldRoots, string newRoot, string settingValue);
    }

    /// <summary>
    /// Moves the icon cache to another folder and points the plugin at it. A move on one volume is
    /// one rename; across volumes it goes file by file, so it never needs room for a second copy.
    /// The journal lets the next launch finish a move that stopped part way.
    /// </summary>
    public sealed class IconCacheRelocationService
    {
        internal const string MovingTextKey = "LOCPlayAch_Settings_IconCache_MoveInProgress";

        private readonly IconCacheLocation _location;
        private readonly IconCacheWriteGate _writeGate;
        private readonly IIconCacheRelocationHost _host;
        private readonly IconCacheMoveEnvironment _environment;
        private readonly ILogger _logger;
        private int _moving;

        // Source files the last run could only copy because another program held them open.
        private List<string> _leftovers = new List<string>();

        public IconCacheRelocationService(
            IconCacheLocation location,
            IconCacheWriteGate writeGate,
            IIconCacheRelocationHost host,
            ILogger logger,
            IconCacheMoveEnvironment environment = null)
        {
            _location = location ?? throw new ArgumentNullException(nameof(location));
            _writeGate = writeGate ?? throw new ArgumentNullException(nameof(writeGate));
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _logger = logger;
            _environment = environment ?? new IconCacheMoveEnvironment();
        }

        /// <summary>Raised when a move starts and when it ends.</summary>
        public event EventHandler MovingChanged;

        public bool IsMoving => Volatile.Read(ref _moving) != 0;

        public IconCacheMovePlan Plan(string target)
        {
            return IconCacheMovePlanner.Plan(_location.Root, target, _environment);
        }

        /// <summary>
        /// The location to run with at startup, from the saved setting and any journal left by a
        /// move. A move that stopped part way leaves the cache closed to writes until
        /// <see cref="ResumeAsync"/> finishes it; a custom folder that is missing leaves it closed
        /// for the session.
        /// </summary>
        public static IconCacheLocation CreateStartupLocation(
            string pluginDataRoot,
            string settingValue,
            out IconCacheRelocationJournal journal)
        {
            journal = IconCacheRelocationJournal.TryRead(pluginDataRoot);
            if (journal?.Phase == IconCacheRelocationPhase.Committed && !string.IsNullOrWhiteSpace(journal.TargetFolder))
            {
                return new IconCacheLocation(pluginDataRoot, journal.TargetFolder);
            }

            if (journal?.Phase == IconCacheRelocationPhase.Moving &&
                !string.IsNullOrWhiteSpace(journal.CurrentRoot) &&
                !string.IsNullOrWhiteSpace(journal.TargetFolder))
            {
                var moving = new IconCacheLocation(pluginDataRoot, journal.CurrentRoot);
                moving.MarkUnavailable();
                return moving;
            }

            journal = null;
            var location = new IconCacheLocation(pluginDataRoot, settingValue);
            if (location.IsCustom && !Directory.Exists(location.Root))
            {
                location.MarkUnavailable();
            }

            return location;
        }

        /// <summary>
        /// Moves the cache as planned. Runs on the calling thread; call it from a progress dialog's
        /// background action. <paramref name="progress"/> gets a status text, the files done and
        /// the file count.
        /// </summary>
        public async Task<IconCacheMoveResult> MoveAsync(
            IconCacheMovePlan plan,
            Action<string, int, int> progress,
            CancellationToken cancel)
        {
            if (plan == null || !plan.IsValid)
            {
                return new IconCacheMoveResult
                {
                    Outcome = IconCacheMoveOutcome.Invalid,
                    Problem = plan?.Problem ?? IconCacheMoveProblem.NoTarget
                };
            }

            if (Interlocked.CompareExchange(ref _moving, 1, 0) != 0)
            {
                return new IconCacheMoveResult { Outcome = IconCacheMoveOutcome.AlreadyMoving };
            }

            var reserved = false;
            try
            {
                reserved = _host.TryReserveRefreshes();
                if (!reserved)
                {
                    return new IconCacheMoveResult { Outcome = IconCacheMoveOutcome.RefreshRunning };
                }

                RaiseMovingChanged();
                await _writeGate.CloseAndDrainAsync(cancel).ConfigureAwait(false);

                if (plan.Strategy == IconCacheMoveStrategy.PerFile)
                {
                    var space = CheckSpace(plan, progress, cancel);
                    if (space != null)
                    {
                        return space;
                    }
                }

                var journal = new IconCacheRelocationJournal
                {
                    Phase = IconCacheRelocationPhase.Moving,
                    CurrentRoot = plan.CurrentRoot,
                    SourceFolder = plan.SourceFolder,
                    TargetFolder = plan.TargetFolder,
                    LinkToRemove = plan.LinkToRemove,
                    Strategy = plan.Strategy,
                    SettingValue = SettingValueFor(plan.TargetFolder)
                };
                journal.Write(_location.PluginDataRoot);

                var targetExisted = Directory.Exists(plan.TargetFolder);
                try
                {
                    MoveFiles(journal, progress, cancel);
                }
                catch (OperationCanceledException)
                {
                    RestoreAfterCancel(journal, targetExisted, progress);
                    return new IconCacheMoveResult { Outcome = IconCacheMoveOutcome.Cancelled };
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    // A file that could not be moved or read: put back what moved, so this session
                    // keeps every icon in one folder. If that fails too, the journal stays and the
                    // next launch finishes the move instead.
                    _logger?.Error(ex, $"Failed to move the icon cache to '{plan.TargetFolder}'; moving the files back.");
                    RestoreAfterCancel(journal, targetExisted, progress);
                    return new IconCacheMoveResult { Outcome = IconCacheMoveOutcome.Failed, Error = ex };
                }

                Commit(journal);
                return new IconCacheMoveResult { Outcome = IconCacheMoveOutcome.Moved };
            }
            catch (OperationCanceledException)
            {
                return new IconCacheMoveResult { Outcome = IconCacheMoveOutcome.Cancelled };
            }
            catch (Exception ex)
            {
                // The journal stays: the next launch finishes the move from where it stopped.
                _logger?.Error(ex, $"Failed to move the icon cache from '{plan.SourceFolder}' to '{plan.TargetFolder}'.");
                return new IconCacheMoveResult { Outcome = IconCacheMoveOutcome.Failed, Error = ex };
            }
            finally
            {
                if (_location.IsAvailable)
                {
                    _writeGate.Open();
                }

                if (reserved)
                {
                    _host.ReleaseRefreshes();
                }

                Interlocked.Exchange(ref _moving, 0);
                RaiseMovingChanged();
            }
        }

        /// <summary>
        /// Finishes a move a previous session started, or applies one it committed. Not
        /// cancellable: the files are split between two folders until it ends.
        /// </summary>
        public IconCacheMoveResult Resume(IconCacheRelocationJournal journal, Action<string, int, int> progress)
        {
            if (journal == null)
            {
                return new IconCacheMoveResult { Outcome = IconCacheMoveOutcome.Invalid };
            }

            Interlocked.Exchange(ref _moving, 1);
            var reserved = _host.TryReserveRefreshes();
            RaiseMovingChanged();
            try
            {
                if (journal.Phase == IconCacheRelocationPhase.Moving)
                {
                    _logger?.Info($"Finishing the icon cache move from '{journal.SourceFolder}' to '{journal.TargetFolder}'.");
                    MoveFiles(journal, progress, CancellationToken.None);
                }

                Commit(journal);
                return new IconCacheMoveResult { Outcome = IconCacheMoveOutcome.Moved };
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed to finish the icon cache move to '{journal.TargetFolder}'.");
                return new IconCacheMoveResult { Outcome = IconCacheMoveOutcome.Failed, Error = ex };
            }
            finally
            {
                if (_location.IsAvailable)
                {
                    _writeGate.Open();
                }

                if (reserved)
                {
                    _host.ReleaseRefreshes();
                }

                Interlocked.Exchange(ref _moving, 0);
                RaiseMovingChanged();
            }
        }

        /// <summary>
        /// Deletes a committed journal once the saved settings name its folder. Called after every
        /// settings save.
        /// </summary>
        public void CompleteCommittedJournal(string savedSettingValue)
        {
            var journal = IconCacheRelocationJournal.TryRead(_location.PluginDataRoot);
            if (journal?.Phase == IconCacheRelocationPhase.Committed &&
                string.Equals(journal.SettingValue ?? string.Empty, savedSettingValue ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            {
                IconCacheRelocationJournal.Delete(_location.PluginDataRoot);
            }
        }

        private string SettingValueFor(string targetFolder)
        {
            return IconCacheLocation.PathsEqual(targetFolder, _location.DefaultRootPath) ? string.Empty : targetFolder;
        }

        private IconCacheMoveResult CheckSpace(IconCacheMovePlan plan, Action<string, int, int> progress, CancellationToken cancel)
        {
            progress?.Invoke(ResourceProvider.GetString(MovingTextKey), 0, 0);
            var size = IconCacheTreeMover.Measure(plan.SourceFolder, cancel);
            try
            {
                var available = FreeBytesAt(plan.TargetFolder);
                if (available == null)
                {
                    return null;
                }

                // Hard links survive only on NTFS and ReFS drives; a network share or another
                // file system gets a full copy of every linked name.
                var root = Path.GetPathRoot(plan.TargetFolder);
                var keepsLinks = false;
                if (!string.IsNullOrEmpty(root) && !root.StartsWith(@"\\", StringComparison.Ordinal))
                {
                    var format = new DriveInfo(root).DriveFormat;
                    keepsLinks = string.Equals(format, "NTFS", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(format, "ReFS", StringComparison.OrdinalIgnoreCase);
                }

                var required = keepsLinks ? size.UniqueBytes : size.TotalBytes;
                if (available.Value < required)
                {
                    return new IconCacheMoveResult
                    {
                        Outcome = IconCacheMoveOutcome.NotEnoughSpace,
                        RequiredBytes = required,
                        AvailableBytes = available.Value
                    };
                }
            }
            catch (Exception ex) when (ex is IOException || ex is ArgumentException || ex is UnauthorizedAccessException)
            {
                _logger?.Debug(ex, $"Could not read the free space at '{plan.TargetFolder}'.");
            }

            return null;
        }

        // Free bytes for the current user at the folder, or its nearest existing parent; works for
        // drive letters and network shares alike.
        private static long? FreeBytesAt(string folder)
        {
            var current = folder;
            while (!string.IsNullOrEmpty(current) && !Directory.Exists(current))
            {
                current = Path.GetDirectoryName(current);
            }

            if (string.IsNullOrEmpty(current))
            {
                return null;
            }

            var probe = current.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? current
                : current + Path.DirectorySeparatorChar;
            return GetDiskFreeSpaceEx(probe, out var available, out _, out _) ? (long)available : (long?)null;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetDiskFreeSpaceEx(
            string directoryName,
            out ulong freeBytesAvailable,
            out ulong totalNumberOfBytes,
            out ulong totalNumberOfFreeBytes);

        private void MoveFiles(IconCacheRelocationJournal journal, Action<string, int, int> progress, CancellationToken cancel)
        {
            if (journal.Strategy == IconCacheMoveStrategy.RelinkOnly || !Directory.Exists(journal.SourceFolder))
            {
                return;
            }

            if (journal.Strategy == IconCacheMoveStrategy.Rename && TryRename(journal))
            {
                return;
            }

            var movingText = ResourceProvider.GetString(MovingTextKey);
            _leftovers = IconCacheTreeMover.MoveTree(
                journal.SourceFolder,
                journal.TargetFolder,
                (done, total) => progress?.Invoke(movingText, done, total),
                cancel).Leftovers;
        }

        // One rename when the target is free; false sends the move file by file, as when a file in
        // the cache is held open.
        private bool TryRename(IconCacheRelocationJournal journal)
        {
            try
            {
                if (Directory.Exists(journal.TargetFolder))
                {
                    if (Directory.EnumerateFileSystemEntries(journal.TargetFolder).Any())
                    {
                        return false;
                    }

                    Directory.Delete(journal.TargetFolder, recursive: false);
                }

                var parent = Path.GetDirectoryName(journal.TargetFolder);
                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                Directory.Move(journal.SourceFolder, journal.TargetFolder);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                _logger?.Info($"Moving the icon cache file by file, since the folder could not be renamed: {ex.Message}");
                journal.Strategy = IconCacheMoveStrategy.PerFile;
                journal.Write(_location.PluginDataRoot);
                return false;
            }
        }

        private void RestoreAfterCancel(IconCacheRelocationJournal journal, bool targetExisted, Action<string, int, int> progress)
        {
            var restoringText = ResourceProvider.GetString(MovingTextKey);
            IconCacheTreeMover.MoveTree(
                journal.TargetFolder,
                journal.SourceFolder,
                (done, total) => progress?.Invoke(restoringText, done, total),
                CancellationToken.None);
            if (targetExisted)
            {
                Directory.CreateDirectory(journal.TargetFolder);
            }

            IconCacheRelocationJournal.Delete(_location.PluginDataRoot);
            _logger?.Info($"Cancelled the icon cache move to '{journal.TargetFolder}'; the files are back in '{journal.SourceFolder}'.");
        }

        // Each step can run again after a crash: rewriting paths finds nothing left to rewrite, and
        // the journal holds the target until the settings are saved.
        private void Commit(IconCacheRelocationJournal journal)
        {
            var oldRoots = new[] { journal.CurrentRoot, journal.SourceFolder }
                .Where(root => !string.IsNullOrWhiteSpace(root))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var saved = _host.CommitStoredPaths(oldRoots, journal.TargetFolder, journal.SettingValue);

            journal.Phase = IconCacheRelocationPhase.Committed;
            journal.Write(_location.PluginDataRoot);

            _location.ChangeRoot(journal.TargetFolder);
            RemoveLink(journal.LinkToRemove);
            if (!IconCacheLocation.PathsEqual(journal.SourceFolder, journal.TargetFolder))
            {
                // The plugin reloaded from the new folder, so files it held in the old one may be
                // free now. Whatever another program still holds stays behind, already copied.
                var held = IconCacheTreeMover.DeleteLeftovers(_leftovers);
                if (held.Count > 0)
                {
                    _logger?.Warn($"{held.Count} file(s) in '{journal.SourceFolder}' are held open by another program and were left there; their copies are in '{journal.TargetFolder}'.");
                }

                IconCacheTreeMover.DeleteEmptyFolders(journal.SourceFolder);
            }

            _leftovers = new List<string>();

            if (saved)
            {
                IconCacheRelocationJournal.Delete(_location.PluginDataRoot);
            }

            _logger?.Info($"The icon cache now lives in '{journal.TargetFolder}'.");
        }

        private void RemoveLink(string link)
        {
            if (string.IsNullOrWhiteSpace(link))
            {
                return;
            }

            try
            {
                // Deleting a junction or folder link removes the link, not the folder it points at.
                if (Directory.Exists(link) && _environment.IsLink(link))
                {
                    Directory.Delete(link, recursive: false);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                _logger?.Warn(ex, $"Could not remove the old icon cache link '{link}'.");
            }
        }

        private void RaiseMovingChanged()
        {
            try
            {
                MovingChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "An icon cache move listener failed.");
            }
        }
    }
}
