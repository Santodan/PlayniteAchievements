using System;
using System.ComponentModel;
using System.Windows;
using Newtonsoft.Json;
using Playnite.SDK;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.GameCustomData;

using ObservableObject = PlayniteAchievements.Common.ObservableObject;

namespace PlayniteAchievements.Views.Settings.Notifications
{
    /// <summary>
    /// The binding root a settings section shows in game mode: its <c>Persisted.X</c> bindings
    /// resolve against the game's snapshot, whose property names match <see cref="PersistedSettings"/>.
    /// </summary>
    public sealed class GameNotificationOverrideContext<T> where T : ObservableObject
    {
        public GameNotificationOverrideContext(T persisted)
        {
            Persisted = persisted;
        }

        public T Persisted { get; }
    }

    /// <summary>
    /// One game's snapshot override for a settings section (behavior or captures): whether the
    /// game has its own copy, the copy shown while editing, and its instant persistence. Without a
    /// copy the section shows the effective inherited values read-only.
    /// </summary>
    internal sealed class GameNotificationOverrideScope<T> : IDisposable where T : ObservableObject
    {
        private readonly GameCustomDataStore _store;
        private readonly Guid _gameId;
        private readonly Func<GameCustomDataFile, T> _read;
        private readonly Action<GameCustomDataFile, T> _write;
        private readonly Func<T> _effective;
        private readonly Func<T, T> _clone;
        private readonly IPlayniteAPI _api;
        private readonly ILogger _logger;
        private T _current;

        public GameNotificationOverrideScope(
            GameCustomDataStore store,
            Guid gameId,
            Func<GameCustomDataFile, T> read,
            Action<GameCustomDataFile, T> write,
            Func<T> effective,
            Func<T, T> clone,
            IPlayniteAPI api,
            ILogger logger)
        {
            _store = store;
            _gameId = gameId;
            _read = read;
            _write = write;
            _effective = effective;
            _clone = clone;
            _api = api;
            _logger = logger;
        }

        /// <summary>Raised after <see cref="Current"/> or <see cref="IsCustomized"/> changed.</summary>
        public event EventHandler Changed;

        public bool IsCustomized { get; private set; }

        /// <summary>The game's own copy when customized, else the effective inherited values.</summary>
        public T Current => _current;

        /// <summary>
        /// Reads the game's copy. Keeps the instance on screen when the stored values match it, so
        /// the store's echo of this scope's own write does not rebind a control mid-edit.
        /// </summary>
        public void Load()
        {
            T stored = null;
            if (_store != null && _store.TryLoad(_gameId, out var data))
            {
                stored = _read(data);
            }

            var customized = stored != null;
            var next = customized ? _clone(stored) : _effective();
            if (_current != null && customized == IsCustomized && SameValues(_current, next))
            {
                return;
            }

            Replace(next, customized);
        }

        /// <summary>
        /// Gives the game its own copy, seeded from the effective values, or removes it after
        /// confirmation. Returns false when the user declined removal.
        /// </summary>
        public bool SetCustomized(bool customized)
        {
            if (customized == IsCustomized)
            {
                return true;
            }

            if (!customized)
            {
                var result = _api?.Dialogs?.ShowMessage(
                    ResourceProvider.GetString("LOCPlayAch_ManageAchievements_Notifications_StopSeparateConfirm"),
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (result != MessageBoxResult.Yes)
                {
                    return false;
                }
            }

            var copy = customized ? _effective() : null;
            Persist(copy);
            Replace(customized ? copy : _effective(), customized);
            return true;
        }

        private void Replace(T next, bool customized)
        {
            if (_current != null)
            {
                _current.PropertyChanged -= OnCurrentPropertyChanged;
            }

            _current = next;
            IsCustomized = customized;
            if (customized && _current != null)
            {
                _current.PropertyChanged += OnCurrentPropertyChanged;
            }

            Changed?.Invoke(this, EventArgs.Empty);
        }

        private void OnCurrentPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (IsCustomized && ReferenceEquals(sender, _current))
            {
                Persist(_current);
            }
        }

        private void Persist(T value)
        {
            if (_store == null)
            {
                return;
            }

            try
            {
                var copy = value != null ? _clone(value) : null;
                _store.Update(
                    _gameId,
                    data => _write(data, copy),
                    affectsSummaryData: false);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed to save notification settings for game {_gameId}.");
            }
        }

        private static bool SameValues(T first, T second)
        {
            return string.Equals(
                JsonConvert.SerializeObject(first),
                JsonConvert.SerializeObject(second),
                StringComparison.Ordinal);
        }

        public void Dispose()
        {
            if (_current != null)
            {
                _current.PropertyChanged -= OnCurrentPropertyChanged;
            }
        }
    }
}
