using PlayniteAchievements.Models.Settings;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace PlayniteAchievements.Services.LinkedFiles
{
    /// <summary>
    /// XML as a table: every element the schema's row element path names is one row, with its
    /// attributes as <c>@name</c> columns, its child elements as columns named by their local
    /// name, its own text as <c>$text</c>, or, for an element that is nothing but text, as
    /// <c>$value</c>. A blank path takes the root's children as the rows.
    /// </summary>
    internal static class XmlTableReader
    {
        public const string TextColumn = "$text";

        public const string ValueColumn = "$value";

        private static readonly XmlReaderSettings ReaderSettings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            XmlResolver = null
        };

        public static LinkedFileReadResult Read(byte[] bytes, LinkedFileSchema schema)
        {
            XDocument document;
            try
            {
                using (var stream = new MemoryStream(bytes ?? Array.Empty<byte>()))
                using (var reader = XmlReader.Create(stream, ReaderSettings))
                {
                    document = XDocument.Load(reader);
                }
            }
            catch (XmlException ex)
            {
                return LinkedFileReadResult.Failed(LinkedFileReadStatus.ReadFailed, "The file is not valid XML: " + ex.Message);
            }

            var root = document.Root;
            if (root == null)
            {
                return LinkedFileReadResult.Failed(LinkedFileReadStatus.ReadFailed, "The file has no root element.");
            }

            var rows = SelectRows(root, schema.RowElementPath);
            if (rows.Count == 0)
            {
                return LinkedFileReadResult.Ok(LinkedFileTable.Empty);
            }

            return LinkedFileReadResult.Ok(ToTable(rows));
        }

        /// <summary>
        /// The elements a dotted path of local names selects. A path may start at the root's own
        /// name or below it; a blank path selects the root's child elements.
        /// </summary>
        public static IReadOnlyList<XElement> SelectRows(XElement root, string path)
        {
            var segments = (path ?? string.Empty)
                .Split(new[] { '.', '/' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(segment => segment.Trim())
                .Where(segment => segment.Length > 0)
                .ToList();

            if (segments.Count == 0)
            {
                return root.Elements().ToList();
            }

            var start = 0;
            if (string.Equals(segments[0], root.Name.LocalName, StringComparison.OrdinalIgnoreCase))
            {
                start = 1;
                if (segments.Count == 1)
                {
                    return new List<XElement> { root };
                }
            }

            IEnumerable<XElement> current = new[] { root };
            for (var i = start; i < segments.Count; i++)
            {
                var name = segments[i];
                current = current.SelectMany(element => element.Elements().Where(child =>
                    string.Equals(child.Name.LocalName, name, StringComparison.OrdinalIgnoreCase)));
            }

            return current.ToList();
        }

        private static LinkedFileTable ToTable(IReadOnlyList<XElement> elements)
        {
            var columns = new List<string>();
            var known = new HashSet<string>(StringComparer.Ordinal);
            var rows = new List<Dictionary<string, string>>();

            void Put(Dictionary<string, string> cells, string column, string text)
            {
                if (known.Add(column))
                {
                    columns.Add(column);
                }

                cells[column] = text;
            }

            foreach (var element in elements)
            {
                var cells = new Dictionary<string, string>(StringComparer.Ordinal);
                var hasAttributes = false;
                foreach (var attribute in element.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration))
                {
                    hasAttributes = true;
                    Put(cells, "@" + attribute.Name.LocalName, attribute.Value);
                }

                var childNames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var hasChildren = false;
                foreach (var child in element.Elements())
                {
                    hasChildren = true;
                    var name = child.Name.LocalName;
                    childNames.TryGetValue(name, out var count);
                    childNames[name] = count + 1;
                    var column = count == 0 ? name : name + " (" + (count + 1) + ")";
                    Put(cells, column, child.Value.Trim());
                }

                var ownText = string.Concat(element.Nodes().OfType<XText>().Select(text => text.Value)).Trim();
                if (!hasAttributes && !hasChildren)
                {
                    Put(cells, ValueColumn, ownText);
                }
                else if (ownText.Length > 0)
                {
                    Put(cells, TextColumn, ownText);
                }

                rows.Add(cells);
            }

            return LinkedFileTable.Build(
                columns,
                rows.Select(cells => columns.Select(column => cells.TryGetValue(column, out var text) ? text : string.Empty)));
        }
    }
}
