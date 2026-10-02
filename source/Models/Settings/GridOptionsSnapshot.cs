using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace PlayniteAchievements.Models.Settings
{
    /// <summary>
    /// Captures a grid options record's values so an editor can apply edits live to the real record
    /// -- keeping the grid behind it in sync -- and still revert on Cancel.
    ///
    /// Restores values into the original instance rather than swapping it, because every live
    /// binding and grid subscription points at that instance; replacing it would leave them reading
    /// an orphan.
    ///
    /// Reflective over public read/write properties on purpose: the records' own Clone methods
    /// assign field by field, so a new option added without touching them is silently dropped. A
    /// snapshot that misses a field would revert it to whatever the record happened to hold, which
    /// is worse than not reverting at all.
    /// </summary>
    internal sealed class GridOptionsSnapshot
    {
        private readonly List<Tuple<object, PropertyInfo, object>> _values =
            new List<Tuple<object, PropertyInfo, object>>();

        /// <summary>
        /// Captures every record given. Nulls are skipped, so callers can pass optional records
        /// (the category sub-grid) without checking.
        /// </summary>
        public GridOptionsSnapshot(params object[] records)
        {
            if (records == null)
            {
                return;
            }

            foreach (var record in records.Where(r => r != null))
            {
                foreach (var property in GetCopyableProperties(record.GetType()))
                {
                    _values.Add(Tuple.Create(record, property, Capture(property.GetValue(record))));
                }
            }
        }

        public void Restore()
        {
            foreach (var entry in _values)
            {
                var current = entry.Item2.GetValue(entry.Item1);
                var captured = Capture(entry.Item3);

                // Assigning unconditionally would raise PropertyChanged for every option and make
                // a cancel look like a full rewrite to anything listening.
                if (!Equals(current, captured))
                {
                    entry.Item2.SetValue(entry.Item1, captured);
                }
            }
        }

        private static IEnumerable<PropertyInfo> GetCopyableProperties(Type type)
        {
            return type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property =>
                    property.CanRead
                    && property.CanWrite
                    && property.GetIndexParameters().Length == 0);
        }

        /// <summary>
        /// Column layout is a mutable object, so it is copied on the way in and again on the way
        /// out; sharing it would let later edits reach into the snapshot.
        /// </summary>
        private static object Capture(object value)
        {
            return value is GridColumnLayoutOptions columns ? columns.Clone() : value;
        }
    }
}
