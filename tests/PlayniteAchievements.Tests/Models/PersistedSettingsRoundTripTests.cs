using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Models.Tests
{
    /// <summary>
    /// Mechanically verifies that every public settable property of PersistedSettings
    /// survives Clone() and CopyFrom(). A property omitted from either copy path silently
    /// resets to its default on the settings apply round-trip, so this test enumerates all
    /// properties via reflection instead of relying on hand-maintained per-property tests.
    /// </summary>
    [TestClass]
    public class PersistedSettingsRoundTripTests
    {
        private static readonly Guid TestGuid = Guid.Parse("6f1e58a6-1d1c-4b1e-9b1a-000000000001");
        private static readonly DateTime TestDate = new DateTime(2024, 3, 4, 5, 6, 7, DateTimeKind.Utc);

        private static readonly JsonSerializerSettings CompareSettings = new JsonSerializerSettings
        {
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
            Formatting = Formatting.None
        };

        [TestMethod]
        public void Clone_PreservesEveryPublicSettableProperty()
        {
            RunRoundTrip(source => source.Clone());
        }

        [TestMethod]
        public void CopyFrom_PreservesEveryPublicSettableProperty()
        {
            RunRoundTrip(source =>
            {
                var target = new PersistedSettings();
                target.CopyFrom(source);
                return target;
            });
        }

        private static void RunRoundTrip(Func<PersistedSettings, PersistedSettings> copy)
        {
            var source = new PersistedSettings();
            var properties = GetSettableProperties();
            var errors = new List<string>();

            foreach (var prop in properties)
            {
                object current;
                try
                {
                    current = prop.GetValue(source);
                }
                catch (Exception ex)
                {
                    errors.Add($"{prop.Name}: getter threw {ex.GetType().Name}: {ex.Message}");
                    continue;
                }

                if (!TryMakeValue(prop.PropertyType, current, out var value))
                {
                    errors.Add($"{prop.Name}: no test value factory for type {prop.PropertyType}. Extend TryMakeValue.");
                    continue;
                }

                try
                {
                    prop.SetValue(source, value);
                }
                catch (Exception ex)
                {
                    errors.Add($"{prop.Name}: setter threw {ex.GetType().Name}: {ex.Message}");
                }
            }

            // Warm-up pass: some nested objects materialize state lazily in getters that run
            // during serialization (e.g. TaggingSettings tag configs), so serialize everything
            // once to stabilize the source before snapshotting expectations.
            foreach (var prop in properties)
            {
                Serialize(prop.GetValue(source));
            }

            // Read expected values after all sets so clamping and cross-property
            // normalization (e.g. friend merge groups) are reflected in expectations.
            var expected = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var prop in properties)
            {
                expected[prop.Name] = Serialize(prop.GetValue(source));
            }

            var result = copy(source);

            foreach (var prop in properties)
            {
                var actual = Serialize(prop.GetValue(result));
                if (!string.Equals(expected[prop.Name], actual, StringComparison.Ordinal))
                {
                    errors.Add($"{prop.Name}: expected {Truncate(expected[prop.Name])} but copy has {Truncate(actual)}");
                }
            }

            if (errors.Count > 0)
            {
                Assert.Fail(
                    $"{errors.Count} propert{(errors.Count == 1 ? "y" : "ies")} failed the copy round-trip:\n" +
                    string.Join("\n", errors));
            }
        }

        private static List<PropertyInfo> GetSettableProperties()
        {
            return typeof(PersistedSettings)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.CanWrite && p.GetSetMethod() != null && p.GetIndexParameters().Length == 0)
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .ToList();
        }

        private static string Serialize(object value)
        {
            return JsonConvert.SerializeObject(value, CompareSettings);
        }

        private static string Truncate(string s)
        {
            return s != null && s.Length > 120 ? s.Substring(0, 120) + "..." : s;
        }

        private static bool TryMakeValue(Type type, object current, out object value)
        {
            value = null;
            var underlying = Nullable.GetUnderlyingType(type) ?? type;

            if (underlying == typeof(bool))
            {
                value = !(current is bool b && b);
                return true;
            }

            if (underlying == typeof(int))
            {
                value = (current is int i ? i : 0) + 3;
                return true;
            }

            if (underlying == typeof(double))
            {
                var d = current is double cd && !double.IsNaN(cd) && !double.IsInfinity(cd) ? cd : 0d;
                value = d > 1000d ? d - 0.125 : d + 32.125;
                return true;
            }

            if (underlying == typeof(string))
            {
                var s = current as string;
                value = string.IsNullOrEmpty(s) ? "round-trip-test" : s + "-rt";
                return true;
            }

            if (underlying.IsEnum)
            {
                foreach (var member in Enum.GetValues(underlying))
                {
                    if (!member.Equals(current))
                    {
                        value = member;
                        return true;
                    }
                }
                return false;
            }

            if (underlying == typeof(Guid))
            {
                value = TestGuid;
                return true;
            }

            if (underlying == typeof(DateTime))
            {
                value = TestDate;
                return true;
            }

            if (underlying == typeof(JObject))
            {
                value = new JObject { ["TestKey"] = "TestValue" };
                return true;
            }

            if (underlying.IsGenericType)
            {
                var genericDef = underlying.GetGenericTypeDefinition();
                var args = underlying.GetGenericArguments();

                if (genericDef == typeof(Dictionary<,>) || genericDef == typeof(IDictionary<,>))
                {
                    var dictType = typeof(Dictionary<,>).MakeGenericType(args);
                    var dict = (IDictionary)Activator.CreateInstance(dictType);
                    if (!TryMakeValue(args[0], null, out var key) || !TryMakeValue(args[1], null, out var elem))
                    {
                        return false;
                    }
                    dict[key] = elem;
                    value = dict;
                    return true;
                }

                if (genericDef == typeof(HashSet<>))
                {
                    var set = Activator.CreateInstance(underlying);
                    if (!TryMakeValue(args[0], null, out var elem))
                    {
                        return false;
                    }
                    underlying.GetMethod("Add").Invoke(set, new[] { elem });
                    value = set;
                    return true;
                }

                if (typeof(IList).IsAssignableFrom(underlying) && args.Length == 1)
                {
                    var list = (IList)Activator.CreateInstance(underlying);
                    if (!TryMakeValue(args[0], null, out var elem))
                    {
                        return false;
                    }
                    list.Add(elem);
                    value = list;
                    return true;
                }
            }

            if (underlying.IsClass && underlying.GetConstructor(Type.EmptyTypes) != null)
            {
                var instance = Activator.CreateInstance(underlying);
                MutateSimpleProperties(instance);
                value = NormalizeViaClone(instance);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Nudges every simple property on a nested settings object away from its default so a
        /// copy path that substitutes a fresh default instance is detected as a mismatch, and so
        /// generated collection entries carry enough data to survive validation-normalization.
        /// </summary>
        private static void MutateSimpleProperties(object instance)
        {
            var props = instance.GetType()
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.CanWrite && p.GetSetMethod() != null && p.GetIndexParameters().Length == 0);

            foreach (var prop in props)
            {
                var t = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
                if (t != typeof(bool) && t != typeof(int) && t != typeof(double) && t != typeof(string) && !t.IsEnum)
                {
                    continue;
                }

                if (!TryMakeValue(prop.PropertyType, prop.GetValue(instance), out var v))
                {
                    continue;
                }

                try
                {
                    prop.SetValue(instance, v);
                }
                catch
                {
                    // Setter rejected the generated value (validation); leave it at default.
                }
            }
        }

        /// <summary>
        /// Passes a generated object through its own Clone() when one exists so the value stored
        /// on the source instance is already in normal form; otherwise a Clone() that performs
        /// migration-style normalization would read as a spurious mismatch.
        /// </summary>
        private static object NormalizeViaClone(object instance)
        {
            var clone = instance.GetType().GetMethod("Clone", Type.EmptyTypes);
            if (clone != null && clone.ReturnType.IsAssignableFrom(instance.GetType()))
            {
                try
                {
                    return clone.Invoke(instance, null) ?? instance;
                }
                catch
                {
                    return instance;
                }
            }

            return instance;
        }
    }
}
