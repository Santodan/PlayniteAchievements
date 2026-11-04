using System;
using System.Collections.Generic;
using System.Linq;
using Playnite.SDK;
using Playnite.SDK.Models;

namespace PlayniteAchievements.Services.Tagging
{
    /// <summary>
    /// One Playnite library field the plugin maintains on games, as the sync code sees it: a
    /// named item collection (tags, features) plus the id list each game carries for it.
    /// </summary>
    internal interface IManagedLibraryField
    {
        /// <summary>The field's noun for log lines, such as "tag".</summary>
        string Noun { get; }

        bool Exists(Guid id);

        /// <summary>The id of the item called <paramref name="name"/>, ignoring case; null when none or blank.</summary>
        Guid? FindByName(string name);

        /// <summary>Creates an item and returns its id; null when the name is blank or the add fails.</summary>
        Guid? Create(string name);

        /// <summary>Renames an item in place. False when it is missing or already has that name.</summary>
        bool Rename(Guid id, string newName);

        /// <summary>Deletes an item. False when it is already gone.</summary>
        bool Remove(Guid id);

        /// <summary>The game's id list for this field; may be null.</summary>
        List<Guid> GetIds(Game game);

        void SetIds(Game game, List<Guid> ids);
    }

    /// <summary>
    /// <see cref="IManagedLibraryField"/> over an <see cref="IItemCollection{TItem}"/> and a pair
    /// of game id-list accessors. <see cref="Tags"/> and <see cref="Features"/> are the two the
    /// plugin maintains.
    /// </summary>
    internal sealed class ItemCollectionField<TItem> : IManagedLibraryField
        where TItem : DatabaseObject, new()
    {
        private readonly IItemCollection<TItem> _items;
        private readonly Func<Game, List<Guid>> _getIds;
        private readonly Action<Game, List<Guid>> _setIds;
        private readonly ILogger _logger;

        public ItemCollectionField(
            string noun,
            IItemCollection<TItem> items,
            Func<Game, List<Guid>> getIds,
            Action<Game, List<Guid>> setIds,
            ILogger logger)
        {
            Noun = noun ?? throw new ArgumentNullException(nameof(noun));
            _items = items ?? throw new ArgumentNullException(nameof(items));
            _getIds = getIds ?? throw new ArgumentNullException(nameof(getIds));
            _setIds = setIds ?? throw new ArgumentNullException(nameof(setIds));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public static IManagedLibraryField Tags(IGameDatabaseAPI database, ILogger logger)
        {
            return new ItemCollectionField<Tag>("tag", database.Tags, game => game.TagIds, (game, ids) => game.TagIds = ids, logger);
        }

        public static IManagedLibraryField Features(IGameDatabaseAPI database, ILogger logger)
        {
            return new ItemCollectionField<GameFeature>("feature", database.Features, game => game.FeatureIds, (game, ids) => game.FeatureIds = ids, logger);
        }

        public string Noun { get; }

        public bool Exists(Guid id) => id != Guid.Empty && _items.Get(id) != null;

        public Guid? FindByName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            return _items.FirstOrDefault(item => item.Name?.Equals(name, StringComparison.OrdinalIgnoreCase) ?? false)?.Id;
        }

        public Guid? Create(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            try
            {
                var item = new TItem { Name = name };
                _items.Add(item);
                _logger.Debug($"Created {Noun} '{name}' with ID {item.Id}");
                return item.Id;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Failed to create {Noun} '{name}'");
                return null;
            }
        }

        public bool Rename(Guid id, string newName)
        {
            var item = id == Guid.Empty ? null : _items.Get(id);
            if (item == null || string.Equals(item.Name, newName, StringComparison.Ordinal))
            {
                return false;
            }

            item.Name = newName;
            _items.Update(item);
            return true;
        }

        public bool Remove(Guid id)
        {
            var item = id == Guid.Empty ? null : _items.Get(id);
            if (item == null)
            {
                return false;
            }

            _items.Remove(item);
            return true;
        }

        public List<Guid> GetIds(Game game) => game == null ? null : _getIds(game);

        public void SetIds(Game game, List<Guid> ids)
        {
            if (game != null)
            {
                _setIds(game, ids);
            }
        }
    }
}
