using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;

namespace PlayniteAchievements.Common
{
    /// <summary>
    /// Copies one instance's state onto another of the same type by assigning backing fields
    /// directly.
    /// </summary>
    /// <remarks>
    /// Exists so a view model can refresh rows that are already bound instead of replacing the
    /// collection they live in. Replacing it raises a Reset, and on the Manage Achievements grid
    /// that measured ~136ms for the DataGrid to react plus ~162ms to re-realize a viewport,
    /// regardless of how little actually changed.
    ///
    /// Fields rather than properties, deliberately. A row's public setters validate, raise
    /// events, depend on each other's assignment order and feed a persistence hook, so driving
    /// ~50 of them across hundreds of rows would risk both a different result than a rebuild and
    /// a storm of store writes. Assigning fields runs none of that, which makes the copy exactly
    /// as complete as the object's own state and no more -- including fields added later, which
    /// a hand-written copy would silently miss.
    /// </remarks>
    internal static class ObservableStateCopier
    {
        private static readonly ConcurrentDictionary<Type, FieldInfo[]> FieldCache =
            new ConcurrentDictionary<Type, FieldInfo[]>();

        /// <summary>
        /// The instance fields <see cref="CopyState{T}"/> assigns: everything the type declares
        /// or inherits, except readonly fields and delegates.
        /// </summary>
        /// <remarks>
        /// Delegates are excluded because an event's backing field holds that instance's
        /// subscribers. Copying it would repoint this object's event at the other object's
        /// listeners, so a refresh would silently redirect notifications.
        ///
        /// Readonly fields are excluded because they are set once in the constructor and are not
        /// state a refresh can change -- collections held in them are still shared by reference,
        /// which is the same thing a rebuilt instance would do.
        /// </remarks>
        public static FieldInfo[] GetStateFields(Type type)
        {
            if (type == null)
            {
                return Array.Empty<FieldInfo>();
            }

            return FieldCache.GetOrAdd(type, Resolve);
        }

        private static FieldInfo[] Resolve(Type type)
        {
            var fields = Enumerable.Empty<FieldInfo>();

            // Walked up the hierarchy explicitly: GetFields does not return private fields
            // declared on a base type, and a base class's state is still state.
            for (var current = type; current != null && current != typeof(object); current = current.BaseType)
            {
                fields = fields.Concat(current.GetFields(
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly));
            }

            return fields
                .Where(field => !field.IsInitOnly)
                .Where(field => !typeof(Delegate).IsAssignableFrom(field.FieldType))
                .ToArray();
        }

        /// <summary>
        /// Whether the two already hold identical state, so a caller can skip both the copy and
        /// the notification that would follow it.
        /// </summary>
        /// <remarks>
        /// Worth checking because the notification is the expensive part, not the copy. Telling
        /// a bound object that every property changed makes the view re-evaluate it, and doing
        /// that for hundreds of objects that did not change costs far more than comparing them.
        /// </remarks>
        public static bool StateEquals<T>(T left, T right)
            where T : class
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            if (left == null || right == null || left.GetType() != right.GetType())
            {
                return false;
            }

            var fields = GetStateFields(left.GetType());
            for (var i = 0; i < fields.Length; i++)
            {
                if (!Equals(fields[i].GetValue(left), fields[i].GetValue(right)))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Makes <paramref name="target"/> hold the same state as <paramref name="source"/>.
        /// Raises nothing; the caller decides how to notify, because only it knows whether the
        /// object is bound to anything.
        /// </summary>
        public static void CopyState<T>(T target, T source)
            where T : class
        {
            if (target == null || source == null || ReferenceEquals(target, source))
            {
                return;
            }

            // The runtime type of the target, so a subclass's own fields are copied too. A
            // source of a different runtime type would leave those fields unreadable, so the
            // copy is refused rather than half-applied.
            var type = target.GetType();
            if (source.GetType() != type)
            {
                throw new ArgumentException(
                    $"Cannot copy state from {source.GetType().Name} onto {type.Name}.",
                    nameof(source));
            }

            var fields = GetStateFields(type);
            for (var i = 0; i < fields.Length; i++)
            {
                fields[i].SetValue(target, fields[i].GetValue(source));
            }
        }
    }
}
