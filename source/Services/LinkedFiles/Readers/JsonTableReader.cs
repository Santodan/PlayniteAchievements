using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PlayniteAchievements.Models.Settings;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace PlayniteAchievements.Services.LinkedFiles
{
    /// <summary>
    /// JSON as a table. An array of objects is one row per object with the keys as columns
    /// (nested objects flatten into dotted names); an object of objects is one row per entry
    /// with the entry's key in a <c>$key</c> column; scalars land in <c>$value</c>. The rows
    /// node is the root or the schema's dotted <c>RootPath</c>.
    /// </summary>
    internal static class JsonTableReader
    {
        public const string KeyColumn = "$key";

        public const string ValueColumn = "$value";

        public static LinkedFileReadResult Read(string text, LinkedFileSchema schema)
        {
            JToken root;
            try
            {
                using (var reader = new JsonTextReader(new StringReader(text ?? string.Empty))
                {
                    DateParseHandling = DateParseHandling.None
                })
                {
                    root = JToken.ReadFrom(reader);
                }
            }
            catch (JsonException ex)
            {
                return LinkedFileReadResult.Failed(LinkedFileReadStatus.ReadFailed, "The file is not valid JSON: " + ex.Message);
            }

            var nodes = SelectPath(root, schema.RootPath);
            if (nodes.Count == 0)
            {
                return LinkedFileReadResult.Failed(LinkedFileReadStatus.SchemaInvalid, "Nothing was found at the rows path.");
            }

            return LinkedFileReadResult.Ok(ToTable(nodes));
        }

        /// <summary>
        /// The nodes a dotted path selects from <paramref name="root"/>: a segment names an
        /// object property (ignoring case), <c>[*]</c> selects every element of an array, and a
        /// number selects an array element. A blank path selects the root.
        /// </summary>
        public static IReadOnlyList<JToken> SelectPath(JToken root, string path)
        {
            var current = new List<JToken> { root };
            if (root == null)
            {
                return Array.Empty<JToken>();
            }

            var trimmed = (path ?? string.Empty).Trim();
            if (trimmed.Length == 0)
            {
                return current;
            }

            foreach (var rawSegment in trimmed.Split('.'))
            {
                var segment = rawSegment.Trim();
                if (segment.Length == 0)
                {
                    continue;
                }

                var next = new List<JToken>();
                var wildcard = segment.EndsWith("[*]", StringComparison.Ordinal);
                var name = wildcard ? segment.Substring(0, segment.Length - 3) : segment;
                foreach (var node in current)
                {
                    var selected = SelectSegment(node, name);
                    if (selected == null)
                    {
                        continue;
                    }

                    if (wildcard)
                    {
                        if (selected is JArray array)
                        {
                            next.AddRange(array.Children());
                        }
                    }
                    else
                    {
                        next.Add(selected);
                    }
                }

                current = next;
                if (current.Count == 0)
                {
                    break;
                }
            }

            return current;
        }

        private static JToken SelectSegment(JToken node, string name)
        {
            if (name.Length == 0)
            {
                return node;
            }

            if (node is JObject obj)
            {
                var property = obj.Properties().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal))
                               ?? obj.Properties().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
                return property?.Value;
            }

            if (node is JArray array && int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
            {
                return index >= 0 && index < array.Count ? array[index] : null;
            }

            return null;
        }

        /// <summary>
        /// Rows from the selected nodes: one node that is an array yields a row per element, one
        /// node that is an object yields a row per property with the property name in
        /// <c>$key</c>, several nodes yield a row each.
        /// </summary>
        public static LinkedFileTable ToTable(IReadOnlyList<JToken> nodes)
        {
            var columns = new List<string>();
            var columnIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            var rows = new List<Dictionary<string, string>>();

            void AddRow(string key, JToken value)
            {
                var cells = new Dictionary<string, string>(StringComparer.Ordinal);
                if (key != null)
                {
                    Put(cells, KeyColumn, key);
                }

                if (value is JObject obj)
                {
                    Flatten(obj, null, cells);
                }
                else if (value is JArray array)
                {
                    Put(cells, ValueColumn, array.ToString(Formatting.None));
                }
                else
                {
                    Put(cells, ValueColumn, ScalarText(value));
                }

                rows.Add(cells);
            }

            void Put(Dictionary<string, string> cells, string column, string text)
            {
                if (!columnIndex.ContainsKey(column))
                {
                    columnIndex[column] = columns.Count;
                    columns.Add(column);
                }

                cells[column] = text;
            }

            void Flatten(JObject obj, string prefix, Dictionary<string, string> cells)
            {
                foreach (var property in obj.Properties())
                {
                    var column = prefix == null ? property.Name : prefix + "." + property.Name;
                    if (property.Value is JObject nested)
                    {
                        Flatten(nested, column, cells);
                    }
                    else if (property.Value is JArray array)
                    {
                        Put(cells, column, array.ToString(Formatting.None));
                    }
                    else
                    {
                        Put(cells, column, ScalarText(property.Value));
                    }
                }
            }

            if (nodes.Count == 1)
            {
                var node = nodes[0];
                if (node is JArray array)
                {
                    foreach (var element in array.Children())
                    {
                        AddRow(null, element);
                    }
                }
                else if (node is JObject obj)
                {
                    foreach (var property in obj.Properties())
                    {
                        AddRow(property.Name, property.Value);
                    }
                }
                else
                {
                    AddRow(null, node);
                }
            }
            else
            {
                foreach (var node in nodes)
                {
                    AddRow(null, node);
                }
            }

            var ordered = new List<string>(columns);
            if (columnIndex.ContainsKey(KeyColumn))
            {
                ordered.Remove(KeyColumn);
                ordered.Insert(0, KeyColumn);
            }

            return LinkedFileTable.Build(
                ordered,
                rows.Select(cells => ordered.Select(column => cells.TryGetValue(column, out var text) ? text : string.Empty)));
        }

        private static string ScalarText(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined)
            {
                return string.Empty;
            }

            if (token is JValue value)
            {
                switch (value.Type)
                {
                    case JTokenType.Boolean:
                        return (bool)value ? "true" : "false";
                    case JTokenType.Date:
                        return value.Value is DateTime date
                            ? date.ToString("o", CultureInfo.InvariantCulture)
                            : Convert.ToString(value.Value, CultureInfo.InvariantCulture);
                    default:
                        return Convert.ToString(value.Value, CultureInfo.InvariantCulture) ?? string.Empty;
                }
            }

            return token.ToString(Formatting.None);
        }
    }
}
