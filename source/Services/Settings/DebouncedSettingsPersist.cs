using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;

namespace PlayniteAchievements.Services.Settings
{
    /// <summary>
    /// Saves settings edited live, outside the settings window's edit transaction.
    ///
    /// Editors that write straight to the persisted tree (the per-grid display settings popup, the
    /// showcase and start page widget options) need three things, and getting any of them wrong is
    /// only visible much later:
    ///
    /// - Debounce. A full settings write per checkbox toggle makes the editor visibly laggy, so a
    ///   burst of edits collapses into one write.
    /// - A flush when the editor goes away, or the last edit in the burst is lost.
    /// - Deference to a pending settings edit session. While a settings window holds an edit
    ///   snapshot, its OK/Cancel decides whether these edits are written; saving underneath it would
    ///   commit changes the user may be about to revert.
    ///
    /// Attach it to the editor control and hand it the records it edits; it hooks Loaded/Unloaded
    /// itself. A caller that flips a value directly instead of handing over a record calls
    /// <see cref="Schedule"/> after the write.
    /// </summary>
    internal sealed class DebouncedSettingsPersist : IDisposable
    {
        private const int DebounceMilliseconds = 600;

        private readonly FrameworkElement _owner;
        private readonly Action _persist;
        private readonly Func<bool> _isDeferred;
        private readonly List<INotifyPropertyChanged> _records = new List<INotifyPropertyChanged>();

        private DispatcherTimer _timer;
        private bool _attached;
        private bool _disposed;

        /// <param name="owner">The editor control; its Loaded/Unloaded drive attach and flush.</param>
        /// <param name="persist">Performs the save.</param>
        /// <param name="isDeferred">
        /// Returns true while something else owns the write (a pending settings edit session). The
        /// pending save is dropped rather than queued: the owner of the transaction will write the
        /// whole tree, including these edits, if the user commits it.
        /// </param>
        public DebouncedSettingsPersist(FrameworkElement owner, Action persist, Func<bool> isDeferred = null)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            _persist = persist ?? throw new ArgumentNullException(nameof(persist));
            _isDeferred = isDeferred;

            _owner.Loaded += OnOwnerLoaded;
            _owner.Unloaded += OnOwnerUnloaded;
            if (_owner.IsLoaded)
            {
                AttachRecords();
            }
        }

        /// <summary>
        /// Watches a record for edits. Safe to call again after the records are replaced, e.g. when
        /// a settings window Cancel swaps the persisted instance: pass the new records after
        /// <see cref="ClearRecords"/>.
        /// </summary>
        public void Watch(object record)
        {
            if (_disposed || !(record is INotifyPropertyChanged observable) || _records.Contains(observable))
            {
                return;
            }

            _records.Add(observable);
            if (_attached)
            {
                observable.PropertyChanged -= OnRecordChanged;
                observable.PropertyChanged += OnRecordChanged;
            }
        }

        /// <summary>
        /// Stops watching every record and drops any pending save without performing it. Used when
        /// the values being edited are about to be replaced or reverted, where writing them would
        /// persist state the user did not ask for.
        /// </summary>
        public void ClearRecords()
        {
            DetachRecords();
            _records.Clear();
            _timer?.Stop();
        }

        /// <summary>
        /// Writes now if a save is pending, unless something else owns the write.
        /// </summary>
        public void Flush()
        {
            if (_timer == null || !_timer.IsEnabled)
            {
                return;
            }

            _timer.Stop();

            if (_isDeferred?.Invoke() == true)
            {
                return;
            }

            _persist();
        }

        /// <summary>
        /// Drops any pending save without performing it.
        /// </summary>
        public void Cancel()
        {
            _timer?.Stop();
        }

        /// <summary>
        /// Writes immediately whether or not a save is pending, unless something else owns the
        /// write. For a revert, where a debounced save may already have committed an intermediate
        /// state that now has to be corrected on disk.
        /// </summary>
        public void PersistNow()
        {
            _timer?.Stop();

            if (_isDeferred?.Invoke() == true)
            {
                return;
            }

            _persist();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _owner.Loaded -= OnOwnerLoaded;
            _owner.Unloaded -= OnOwnerUnloaded;
            DetachRecords();
            _records.Clear();
            _timer?.Stop();
            _timer = null;
        }

        private void OnOwnerLoaded(object sender, RoutedEventArgs e)
        {
            AttachRecords();
        }

        private void OnOwnerUnloaded(object sender, RoutedEventArgs e)
        {
            DetachRecords();
            _attached = false;
            Flush();
        }

        private void AttachRecords()
        {
            _attached = true;
            foreach (var record in _records)
            {
                record.PropertyChanged -= OnRecordChanged;
                record.PropertyChanged += OnRecordChanged;
            }
        }

        private void DetachRecords()
        {
            foreach (var record in _records)
            {
                record.PropertyChanged -= OnRecordChanged;
            }
        }

        /// <summary>
        /// Starts or restarts the debounce window for an edit made outside the watched records.
        /// </summary>
        public void Schedule()
        {
            if (_disposed)
            {
                return;
            }

            if (_timer == null)
            {
                _timer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(DebounceMilliseconds)
                };
                _timer.Tick += (_, __) => Flush();
            }

            // Restart the window on every edit so a burst of toggles produces one write.
            _timer.Stop();
            _timer.Start();
        }

        private void OnRecordChanged(object sender, PropertyChangedEventArgs e)
        {
            Schedule();
        }
    }
}
