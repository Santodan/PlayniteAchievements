using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Notifications;
using PlayniteAchievements.Services.Showcase;
using PlayniteAchievements.Services.Sound;
using PlayniteAchievements.Services.Workshop;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;

namespace PlayniteAchievements.Services.Tests
{
    /// <summary>
    /// Keeps WorkshopPackageFields.json equal to the JSON fields the package writers can emit. The
    /// Workshop validator carries a byte-identical copy and rejects any field it does not list, so a
    /// change to a package model needs this file regenerated and copied to the Workshop repo.
    /// Regenerate by running this test with PA_UPDATE_WORKSHOP_FIELDS=1.
    /// </summary>
    [TestClass]
    public class WorkshopPackageFieldsTests
    {
        private const string FileName = "WorkshopPackageFields.json";
        private const string UpdateVariable = "PA_UPDATE_WORKSHOP_FIELDS";

        private static readonly (string Entry, Type Root)[] Roots =
        {
            (ColorPackPortableStore.ManifestEntryName, typeof(ColorPackFile)),
            (NotificationStylePortableStore.ManifestEntryName, typeof(NotificationStylePortableFile)),
            (ShowcasePagePortableStore.ManifestEntryName, typeof(ShowcasePagePortableFile)),
            (UnlockSoundPortableStore.ManifestEntryName, typeof(UnlockSoundPackFile)),
            (BundlePortableStore.ManifestEntryName, typeof(BundleFile)),
            (GameCustomDataStore.PortablePackageManifestEntryName, typeof(GameCustomDataPortableFile))
        };

        /// <summary>
        /// Fields a released version still writes after the models dropped them. The validator keeps
        /// accepting them, so users on that release can still share; this extension ignores them on read.
        /// </summary>
        private static readonly (Type Owner, string Field, string Since)[] RetiredFields =
        {
            (typeof(ManualAchievementLink), "AllowUnauthenticatedSchemaFetch", "4.1.1")
        };

        [TestMethod]
        public void CommittedFieldList_MatchesPackageModels()
        {
            var expected = Generate();
            var path = Path.Combine(SourceDirectory(), FileName);

            if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
            {
                File.WriteAllText(path, expected);
                return;
            }

            var actual = File.Exists(path) ? File.ReadAllText(path).Replace("\r\n", "\n") : string.Empty;
            Assert.AreEqual(
                expected,
                actual,
                $"{FileName} is out of date with the package models. Run this test with {UpdateVariable}=1, " +
                "then copy the file to tools/Validator/package-fields.json in the Workshop repo.");
        }

        [TestMethod]
        public void Generate_DescribesDictionariesAsMapsAndListsAsLists()
        {
            var types = (JObject)JObject.Parse(Generate())["types"];
            var portable = (JObject)types[nameof(GameCustomDataPortableFile)];

            Assert.AreEqual(nameof(AchievementOverride), (string)portable["AchievementOverrides"]["map"]);
            Assert.AreEqual("value", (string)portable["AchievementOrder"]["list"]);
            Assert.AreEqual(nameof(PortableGameKey), (string)portable["GameKeys"]["list"]);
            Assert.IsNull(portable[nameof(GameCustomDataFile.ExcludedFromRefreshes)]);
        }

        [TestMethod]
        public void Generate_KeepsFieldsReleasedVersionsStillWrite()
        {
            var types = (JObject)JObject.Parse(Generate())["types"];

            Assert.AreEqual("value", (string)types[nameof(ManualAchievementLink)]["AllowUnauthenticatedSchemaFetch"]);
            Assert.AreEqual(nameof(MetadataLinkData), (string)types[nameof(GameCustomDataPortableFile)]["MetadataLinks"]["map"]);
        }

        internal static string Generate()
        {
            var resolver = new DefaultContractResolver();
            var types = new SortedDictionary<string, SortedDictionary<string, JToken>>(StringComparer.Ordinal);
            var fullNames = new Dictionary<string, string>(StringComparer.Ordinal);

            JToken Describe(Type type)
            {
                switch (resolver.ResolveContract(type))
                {
                    case JsonPrimitiveContract _:
                        return "value";
                    case JsonDictionaryContract dictionary:
                        return new JObject { ["map"] = Describe(dictionary.DictionaryValueType) };
                    case JsonArrayContract array:
                        return new JObject { ["list"] = Describe(array.CollectionItemType) };
                    case JsonObjectContract obj:
                        return DescribeObject(type, obj);
                    default:
                        throw new InvalidOperationException($"{type.FullName} has a contract the field list cannot describe.");
                }
            }

            JToken DescribeObject(Type type, JsonObjectContract contract)
            {
                if (fullNames.TryGetValue(type.Name, out var known))
                {
                    if (known != type.FullName)
                    {
                        throw new InvalidOperationException($"Two package types are named {type.Name}: {known} and {type.FullName}.");
                    }

                    return type.Name;
                }

                fullNames[type.Name] = type.FullName;
                var fields = new SortedDictionary<string, JToken>(StringComparer.Ordinal);
                types[type.Name] = fields;

                foreach (var property in contract.Properties)
                {
                    // A get-only scalar is written but never read back, so it is not part of the format.
                    if (property.Ignored || !property.Readable ||
                        (!property.Writable && resolver.ResolveContract(property.PropertyType) is JsonPrimitiveContract))
                    {
                        continue;
                    }

                    fields[property.PropertyName] = Describe(property.PropertyType);
                }

                return type.Name;
            }

            var roots = new JObject();
            foreach (var (entry, root) in Roots)
            {
                roots[entry] = Describe(root);
            }

            foreach (var (owner, field, _) in RetiredFields)
            {
                if (!types.TryGetValue(owner.Name, out var fields))
                {
                    throw new InvalidOperationException($"Retired field {owner.Name}.{field} belongs to a type no package reaches.");
                }

                if (!fields.ContainsKey(field))
                {
                    fields[field] = "value";
                }
            }

            var document = new JObject
            {
                ["roots"] = roots,
                ["types"] = new JObject(types.Select(t => new JProperty(t.Key, new JObject(t.Value.Select(f => new JProperty(f.Key, f.Value))))))
            };

            return document.ToString(Formatting.Indented).Replace("\r\n", "\n") + "\n";
        }

        private static string SourceDirectory([CallerFilePath] string path = null) => Path.GetDirectoryName(path);
    }
}
