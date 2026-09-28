using System;

namespace PlayniteAchievements.Common
{
    /// <summary>
    /// Centralized utilities for DateTime conversions and normalization.
    /// Ensures consistent handling of DateTimeKind across the application.
    /// </summary>
    public static class DateTimeUtilities
    {
        /// <summary>
        /// Ensures a DateTime value has Utc kind, converting from Local or Unspecified as needed.
        /// </summary>
        public static DateTime AsUtcKind(DateTime dt)
        {
            if (dt.Kind == DateTimeKind.Utc) return dt;
            if (dt.Kind == DateTimeKind.Local) return dt.ToUniversalTime();
            return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
        }

        /// <summary>
        /// Ensures a nullable DateTime value has Utc kind.
        /// </summary>
        public static DateTime? AsUtcKind(DateTime? dt) => dt.HasValue ? AsUtcKind(dt.Value) : (DateTime?)null;

        /// <summary>
        /// Converts a DateTime from Utc to Local time, handling all DateTimeKind cases.
        /// </summary>
        public static DateTime AsLocalFromUtc(DateTime dt)
        {
            if (dt.Kind == DateTimeKind.Local) return dt;
            if (dt.Kind == DateTimeKind.Utc) return dt.ToLocalTime();
            return DateTime.SpecifyKind(dt, DateTimeKind.Utc).ToLocalTime();
        }

        /// <summary>
        /// Converts a nullable DateTime from Utc to Local time.
        /// </summary>
        public static DateTime? AsLocalFromUtc(DateTime? dt) => dt.HasValue ? AsLocalFromUtc(dt.Value) : (DateTime?)null;

        /// <summary>
        /// Local calendar day an instant falls on: midnight, Kind Unspecified. Unspecified input is
        /// treated as UTC. This is the day key for unlock-count buckets.
        /// </summary>
        public static DateTime ToLocalDay(DateTime utc) => ToLocalDay(utc, TimeZoneInfo.Local);

        /// <summary>
        /// Calendar day of an instant in <paramref name="zone"/>: midnight, Kind Unspecified.
        /// </summary>
        public static DateTime ToLocalDay(DateTime utc, TimeZoneInfo zone)
        {
            var instant = AsUtcKind(utc);
            var local = TimeZoneInfo.ConvertTimeFromUtc(instant, zone ?? TimeZoneInfo.Local);
            return DateTime.SpecifyKind(local.Date, DateTimeKind.Unspecified);
        }
    }
}
