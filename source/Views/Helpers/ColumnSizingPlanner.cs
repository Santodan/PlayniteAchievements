using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>
    /// Pure column sizing planner. It has no WPF dependency so resize behavior can be
    /// tested independently from DataGrid realization and dispatcher timing.
    /// </summary>
    public static class ColumnSizingPlanner
    {
        public const double LayoutEpsilon = 0.2d;

        public static bool TryPlan(
            IReadOnlyList<string> keys,
            IReadOnlyList<double> seedWidths,
            IReadOnlyList<double> floorWidths,
            string protectedKey,
            string preferredAbsorberKey,
            bool rescaleAll,
            double targetWidth,
            out Dictionary<string, double> plannedWidths)
        {
            plannedWidths = null;
            if (keys == null ||
                seedWidths == null ||
                floorWidths == null ||
                keys.Count == 0 ||
                keys.Count != seedWidths.Count ||
                keys.Count != floorWidths.Count ||
                !IsValidWidth(targetWidth))
            {
                return false;
            }

            var normalizedKeys = new List<string>(keys.Count);
            var floors = new List<double>(keys.Count);
            var widths = new List<double>(keys.Count);

            for (var i = 0; i < keys.Count; i++)
            {
                var key = (keys[i] ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(key))
                {
                    return false;
                }

                var floor = IsValidWidth(floorWidths[i]) ? floorWidths[i] : 1d;
                var seed = IsValidWidth(seedWidths[i]) ? seedWidths[i] : floor;
                normalizedKeys.Add(key);
                floors.Add(Math.Max(1d, floor));
                widths.Add(Math.Max(floor, seed));
            }

            if (floors.Sum() > targetWidth)
            {
                ScaleFloorsToTarget(floors, targetWidth);
                for (var i = 0; i < widths.Count; i++)
                {
                    widths[i] = Math.Max(floors[i], Math.Min(widths[i], targetWidth));
                }
            }

            var totalWidth = widths.Sum();
            var delta = targetWidth - totalWidth;
            if (Math.Abs(delta) > LayoutEpsilon)
            {
                if (rescaleAll || string.IsNullOrWhiteSpace(protectedKey))
                {
                    RescaleProportionally(widths, floors, targetWidth);
                }
                else
                {
                    DistributeDelta(widths, floors, normalizedKeys, protectedKey, preferredAbsorberKey, delta, targetWidth);
                }
            }

            plannedWidths = RoundToTarget(normalizedKeys, widths, floors, protectedKey, preferredAbsorberKey, targetWidth);
            return true;
        }

        public static List<int> BuildAbsorberOrder(
            IReadOnlyList<string> keys,
            string protectedKey,
            string preferredAbsorberKey)
        {
            var order = new List<int>();
            if (keys == null || keys.Count == 0)
            {
                return order;
            }

            var protectedIndex = IndexOfKey(keys, protectedKey);

            Action<int> addIfAvailable = index =>
            {
                if (index < 0 ||
                    index >= keys.Count ||
                    index == protectedIndex ||
                    order.Contains(index))
                {
                    return;
                }

                order.Add(index);
            };

            var preferredIndex = IndexOfKey(keys, preferredAbsorberKey);
            addIfAvailable(preferredIndex);

            if (protectedIndex >= 0)
            {
                for (var offset = 1; offset < keys.Count; offset++)
                {
                    addIfAvailable(protectedIndex + offset);
                    addIfAvailable(protectedIndex - offset);
                }
            }
            else
            {
                for (var i = 0; i < keys.Count; i++)
                {
                    addIfAvailable(i);
                }
            }

            if (order.Count == 0)
            {
                order.Add(keys.Count - 1);
            }

            return order;
        }

        public static bool KeysEqual(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        public static void RescaleProportionally(
            IList<double> widths,
            IReadOnlyList<double> floorWidths,
            double targetWidth)
        {
            if (widths == null ||
                floorWidths == null ||
                widths.Count == 0 ||
                widths.Count != floorWidths.Count ||
                !IsValidWidth(targetWidth))
            {
                return;
            }

            var remainingTarget = targetWidth;
            var remainingWeight = widths.Sum(w => Math.Max(1d, w));
            var remainingMinimum = floorWidths.Sum();

            for (var i = 0; i < widths.Count; i++)
            {
                var floor = floorWidths[i];
                var weight = Math.Max(1d, widths[i]);
                remainingMinimum -= floor;
                var next = i == widths.Count - 1
                    ? remainingTarget
                    : remainingTarget * (weight / remainingWeight);

                next = Math.Max(floor, next);
                next = Math.Min(next, remainingTarget - remainingMinimum);
                widths[i] = next;

                remainingTarget -= next;
                remainingWeight -= weight;
            }
        }

        private static void ScaleFloorsToTarget(IList<double> floors, double targetWidth)
        {
            if (floors == null || floors.Count == 0 || !IsValidWidth(targetWidth))
            {
                return;
            }

            var total = floors.Sum();
            if (!IsValidWidth(total))
            {
                return;
            }

            var minimum = Math.Max(1d, targetWidth / floors.Count);
            for (var i = 0; i < floors.Count; i++)
            {
                floors[i] = Math.Max(1d, Math.Min(floors[i], floors[i] * targetWidth / total));
                if (floors[i] > minimum && floors.Sum() > targetWidth)
                {
                    floors[i] = minimum;
                }
            }

            var delta = targetWidth - floors.Sum();
            if (Math.Abs(delta) <= LayoutEpsilon)
            {
                return;
            }

            for (var i = floors.Count - 1; i >= 0 && Math.Abs(delta) > LayoutEpsilon; i--)
            {
                if (delta > 0)
                {
                    floors[i] += delta;
                    break;
                }

                var take = Math.Min(floors[i] - 1d, -delta);
                if (take <= 0)
                {
                    continue;
                }

                floors[i] -= take;
                delta += take;
            }
        }

        private static void DistributeDelta(
            IList<double> widths,
            IReadOnlyList<double> floorWidths,
            IReadOnlyList<string> keys,
            string protectedKey,
            string preferredAbsorberKey,
            double delta,
            double targetWidth)
        {
            var absorberOrder = BuildAbsorberOrder(keys, protectedKey, preferredAbsorberKey);
            if (absorberOrder.Count == 0)
            {
                return;
            }

            if (delta > 0)
            {
                widths[absorberOrder[0]] += delta;
                return;
            }

            foreach (var index in absorberOrder)
            {
                var capacity = widths[index] - floorWidths[index];
                if (capacity <= 0)
                {
                    continue;
                }

                var take = Math.Min(capacity, -delta);
                widths[index] -= take;
                delta += take;
                if (delta >= -LayoutEpsilon)
                {
                    return;
                }
            }

            var protectedIndex = IndexOfKey(keys, protectedKey);
            if (protectedIndex >= 0 && delta < -LayoutEpsilon)
            {
                var capacity = widths[protectedIndex] - floorWidths[protectedIndex];
                if (capacity > 0)
                {
                    var take = Math.Min(capacity, -delta);
                    widths[protectedIndex] -= take;
                    delta += take;
                }
            }

            if (delta < -LayoutEpsilon)
            {
                RescaleProportionally(widths, floorWidths, targetWidth);
            }
        }

        private static Dictionary<string, double> RoundToTarget(
            IReadOnlyList<string> keys,
            IReadOnlyList<double> widths,
            IReadOnlyList<double> floorWidths,
            string protectedKey,
            string preferredAbsorberKey,
            double targetWidth)
        {
            var roundedWidths = new List<double>(keys.Count);
            var roundedFloors = new List<double>(keys.Count);
            for (var i = 0; i < keys.Count; i++)
            {
                var roundedFloor = RoundPixelWidth(floorWidths[i]);
                roundedFloors.Add(roundedFloor);
                roundedWidths.Add(Math.Max(roundedFloor, RoundPixelWidth(widths[i])));
            }

            var delta = RoundPixelWidth(targetWidth) - roundedWidths.Sum();
            if (Math.Abs(delta) > LayoutEpsilon)
            {
                DistributeRoundedDelta(roundedWidths, roundedFloors, keys, protectedKey, preferredAbsorberKey, delta);
            }

            var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < keys.Count; i++)
            {
                result[keys[i]] = Math.Max(roundedFloors[i], roundedWidths[i]);
            }

            return result;
        }

        private static void DistributeRoundedDelta(
            IList<double> widths,
            IReadOnlyList<double> floorWidths,
            IReadOnlyList<string> keys,
            string protectedKey,
            string preferredAbsorberKey,
            double delta)
        {
            var absorberOrder = BuildAbsorberOrder(keys, protectedKey, preferredAbsorberKey);
            if (absorberOrder.Count == 0)
            {
                return;
            }

            if (delta > 0)
            {
                widths[absorberOrder[0]] += delta;
                return;
            }

            foreach (var index in absorberOrder)
            {
                var capacity = widths[index] - floorWidths[index];
                if (capacity <= 0)
                {
                    continue;
                }

                var take = Math.Min(capacity, -delta);
                widths[index] -= take;
                delta += take;
                if (delta >= -LayoutEpsilon)
                {
                    return;
                }
            }
        }

        private static int IndexOfKey(IReadOnlyList<string> keys, string key)
        {
            if (keys == null || string.IsNullOrWhiteSpace(key))
            {
                return -1;
            }

            for (var i = 0; i < keys.Count; i++)
            {
                if (KeysEqual(keys[i], key))
                {
                    return i;
                }
            }

            return -1;
        }

        private static bool IsValidWidth(double width)
        {
            return !double.IsNaN(width) && !double.IsInfinity(width) && width > 0;
        }

        private static double RoundPixelWidth(double width)
        {
            return IsValidWidth(width)
                ? Math.Max(1d, Math.Round(width, MidpointRounding.AwayFromZero))
                : 0d;
        }
    }
}
