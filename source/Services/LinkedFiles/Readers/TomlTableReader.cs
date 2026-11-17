using Newtonsoft.Json.Linq;
using PlayniteAchievements.Models.Settings;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PlayniteAchievements.Services.LinkedFiles
{
    /// <summary>
    /// TOML as a table. The document is parsed into the same object tree JSON produces, so the
    /// rows path and the row shapes are those of <see cref="JsonTableReader"/>: a table of
    /// sub-tables is one row per sub-table with its name in <c>$key</c>, an array of tables is
    /// one row per element.
    /// </summary>
    internal static class TomlTableReader
    {
        public static LinkedFileReadResult Read(string text, LinkedFileSchema schema)
        {
            JObject root;
            try
            {
                root = TomlParser.Parse(text ?? string.Empty);
            }
            catch (TomlParseException ex)
            {
                return LinkedFileReadResult.Failed(LinkedFileReadStatus.ReadFailed, ex.Message);
            }

            var rowPaths = JsonTableReader.ListRowPaths(root);
            var nodes = JsonTableReader.SelectPath(root, schema.RootPath);
            if (nodes.Count == 0)
            {
                return LinkedFileReadResult.Failed(LinkedFileReadStatus.SchemaInvalid, "Nothing was found at the rows path.", rowPaths: rowPaths);
            }

            return LinkedFileReadResult.Ok(JsonTableReader.ToTable(nodes), rowPaths: rowPaths);
        }
    }

    internal sealed class TomlParseException : Exception
    {
        public TomlParseException(int line, string message)
            : base("The file is not valid TOML (line " + line.ToString(CultureInfo.InvariantCulture) + "): " + message)
        {
        }
    }

    /// <summary>
    /// A TOML reader for the shapes progress files use: comments, bare and quoted keys, dotted
    /// keys, tables, arrays of tables, inline tables, strings (basic, literal, multi-line),
    /// integers, floats, booleans, arrays, and dates and times kept as text.
    /// </summary>
    internal sealed class TomlParser
    {
        private readonly string _text;
        private readonly JObject _root = new JObject();
        private readonly HashSet<JObject> _arrayTableElements = new HashSet<JObject>();
        private int _pos;
        private int _line = 1;
        private JObject _current;

        private TomlParser(string text)
        {
            _text = text;
            _current = _root;
        }

        public static JObject Parse(string text)
        {
            var parser = new TomlParser(text ?? string.Empty);
            parser.ParseDocument();
            return parser._root;
        }

        private void ParseDocument()
        {
            while (true)
            {
                SkipWhitespaceAndComments(includeNewlines: true);
                if (AtEnd)
                {
                    return;
                }

                if (Peek == '[')
                {
                    ParseTableHeader();
                }
                else
                {
                    ParseKeyValue(_current);
                }

                SkipWhitespaceAndComments(includeNewlines: false);
                if (!AtEnd && !AtNewline)
                {
                    throw Error("Unexpected text after a value.");
                }
            }
        }

        private void ParseTableHeader()
        {
            Expect('[');
            var isArray = !AtEnd && Peek == '[';
            if (isArray)
            {
                _pos++;
            }

            SkipSpaces();
            var key = ParseKeyPath();
            SkipSpaces();
            Expect(']');
            if (isArray)
            {
                Expect(']');
            }

            var parent = _root;
            for (var i = 0; i < key.Count - 1; i++)
            {
                parent = Descend(parent, key[i]);
            }

            var last = key[key.Count - 1];
            if (isArray)
            {
                var existing = parent[last];
                JArray array;
                if (existing == null)
                {
                    array = new JArray();
                    parent[last] = array;
                }
                else if (existing is JArray found && IsArrayOfTables(found))
                {
                    array = found;
                }
                else
                {
                    throw Error("'" + last + "' is already defined as something other than an array of tables.");
                }

                var element = new JObject();
                _arrayTableElements.Add(element);
                array.Add(element);
                _current = element;
            }
            else
            {
                _current = Descend(parent, last);
            }
        }

        private bool IsArrayOfTables(JArray array)
        {
            return array.Count == 0 || (array[array.Count - 1] is JObject lastElement && _arrayTableElements.Contains(lastElement));
        }

        private JObject Descend(JObject parent, string key)
        {
            var existing = parent[key];
            if (existing == null)
            {
                var created = new JObject();
                parent[key] = created;
                return created;
            }

            if (existing is JObject obj)
            {
                return obj;
            }

            if (existing is JArray array && array.Count > 0 && array[array.Count - 1] is JObject lastElement && _arrayTableElements.Contains(lastElement))
            {
                return lastElement;
            }

            throw Error("'" + key + "' is already defined as a value.");
        }

        private void ParseKeyValue(JObject target)
        {
            var key = ParseKeyPath();
            SkipSpaces();
            Expect('=');
            SkipSpaces();
            var value = ParseValue();

            var parent = target;
            for (var i = 0; i < key.Count - 1; i++)
            {
                parent = Descend(parent, key[i]);
            }

            var last = key[key.Count - 1];
            if (parent[last] != null)
            {
                throw Error("'" + last + "' is defined twice.");
            }

            parent[last] = value;
        }

        private List<string> ParseKeyPath()
        {
            var parts = new List<string>();
            while (true)
            {
                SkipSpaces();
                parts.Add(ParseSimpleKey());
                SkipSpaces();
                if (!AtEnd && Peek == '.')
                {
                    _pos++;
                    continue;
                }

                return parts;
            }
        }

        private string ParseSimpleKey()
        {
            if (AtEnd)
            {
                throw Error("A key was expected.");
            }

            if (Peek == '"')
            {
                return ParseBasicString(multiLine: false);
            }

            if (Peek == '\'')
            {
                return ParseLiteralString(multiLine: false);
            }

            var start = _pos;
            while (!AtEnd && IsBareKeyChar(Peek))
            {
                _pos++;
            }

            if (_pos == start)
            {
                throw Error("A key was expected.");
            }

            return _text.Substring(start, _pos - start);
        }

        private static bool IsBareKeyChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_' || c == '-';
        }

        private JToken ParseValue()
        {
            if (AtEnd)
            {
                throw Error("A value was expected.");
            }

            var c = Peek;
            if (c == '"')
            {
                return new JValue(StartsWith("\"\"\"") ? ParseBasicString(multiLine: true) : ParseBasicString(multiLine: false));
            }

            if (c == '\'')
            {
                return new JValue(StartsWith("'''") ? ParseLiteralString(multiLine: true) : ParseLiteralString(multiLine: false));
            }

            if (c == '[')
            {
                return ParseArray();
            }

            if (c == '{')
            {
                return ParseInlineTable();
            }

            if (StartsWith("true") && !IsBareKeyCharAt(_pos + 4))
            {
                _pos += 4;
                return new JValue(true);
            }

            if (StartsWith("false") && !IsBareKeyCharAt(_pos + 5))
            {
                _pos += 5;
                return new JValue(false);
            }

            return ParseNumberOrDateTime();
        }

        private bool IsBareKeyCharAt(int index)
        {
            return index < _text.Length && IsBareKeyChar(_text[index]);
        }

        private JArray ParseArray()
        {
            Expect('[');
            var array = new JArray();
            while (true)
            {
                SkipWhitespaceAndComments(includeNewlines: true);
                if (AtEnd)
                {
                    throw Error("An array was not closed.");
                }

                if (Peek == ']')
                {
                    _pos++;
                    return array;
                }

                array.Add(ParseValue());
                SkipWhitespaceAndComments(includeNewlines: true);
                if (AtEnd)
                {
                    throw Error("An array was not closed.");
                }

                if (Peek == ',')
                {
                    _pos++;
                    continue;
                }

                if (Peek == ']')
                {
                    _pos++;
                    return array;
                }

                throw Error("A comma or closing bracket was expected in an array.");
            }
        }

        private JObject ParseInlineTable()
        {
            Expect('{');
            var table = new JObject();
            SkipSpaces();
            if (!AtEnd && Peek == '}')
            {
                _pos++;
                return table;
            }

            while (true)
            {
                SkipSpaces();
                ParseKeyValue(table);
                SkipSpaces();
                if (AtEnd)
                {
                    throw Error("An inline table was not closed.");
                }

                if (Peek == ',')
                {
                    _pos++;
                    continue;
                }

                if (Peek == '}')
                {
                    _pos++;
                    return table;
                }

                throw Error("A comma or closing brace was expected in an inline table.");
            }
        }

        private JToken ParseNumberOrDateTime()
        {
            var start = _pos;
            while (!AtEnd && IsTokenChar(Peek))
            {
                _pos++;
            }

            // A date followed by a space and a time is one value.
            if (_pos - start == 10 && _text[start + 4] == '-' && _pos + 1 < _text.Length && _text[_pos] == ' ' && char.IsDigit(_text[_pos + 1]))
            {
                _pos++;
                while (!AtEnd && IsTokenChar(Peek))
                {
                    _pos++;
                }
            }

            var token = _text.Substring(start, _pos - start);
            if (token.Length == 0)
            {
                throw Error("A value was expected.");
            }

            if (LooksLikeDateTime(token))
            {
                return new JValue(token);
            }

            var cleaned = token.Replace("_", string.Empty);
            if (cleaned.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                return new JValue(Convert.ToInt64(cleaned.Substring(2), 16));
            }

            if (cleaned.StartsWith("0o", StringComparison.OrdinalIgnoreCase))
            {
                return new JValue(Convert.ToInt64(cleaned.Substring(2), 8));
            }

            if (cleaned.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
            {
                return new JValue(Convert.ToInt64(cleaned.Substring(2), 2));
            }

            if (long.TryParse(cleaned, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer))
            {
                return new JValue(integer);
            }

            if (ulong.TryParse(cleaned, NumberStyles.None, CultureInfo.InvariantCulture, out var unsigned))
            {
                return new JValue(unsigned);
            }

            if (IsAllDigits(cleaned))
            {
                // Beyond 64 bits the digits are kept as written.
                return new JValue(cleaned);
            }

            var lower = cleaned.ToLowerInvariant();
            if (lower == "inf" || lower == "+inf")
            {
                return new JValue(double.PositiveInfinity);
            }

            if (lower == "-inf")
            {
                return new JValue(double.NegativeInfinity);
            }

            if (lower == "nan" || lower == "+nan" || lower == "-nan")
            {
                return new JValue(double.NaN);
            }

            if (double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                return new JValue(number);
            }

            throw Error("'" + token + "' is not a value.");
        }

        private static bool IsAllDigits(string value)
        {
            if (value.Length == 0)
            {
                return false;
            }

            foreach (var c in value)
            {
                if (!char.IsDigit(c))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsTokenChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '+' || c == '.' || c == ':' || c == 'T' || c == 'Z';
        }

        private static bool LooksLikeDateTime(string token)
        {
            if (token.Length >= 10 && char.IsDigit(token[0]) && token[4] == '-' && token[7] == '-')
            {
                return true;
            }

            return token.Length >= 5 && char.IsDigit(token[0]) && token.Length > 2 && token[2] == ':';
        }

        private string ParseBasicString(bool multiLine)
        {
            var quote = multiLine ? "\"\"\"" : "\"";
            ExpectText(quote);
            if (multiLine)
            {
                SkipOneNewline();
            }

            var builder = new StringBuilder();
            while (true)
            {
                if (AtEnd)
                {
                    throw Error("A string was not closed.");
                }

                if (StartsWith(quote))
                {
                    _pos += quote.Length;
                    if (multiLine)
                    {
                        // Up to two extra quotes belong to the content.
                        while (!AtEnd && Peek == '"' && builder.Length >= 0)
                        {
                            builder.Append('"');
                            _pos++;
                        }
                    }

                    return builder.ToString();
                }

                var c = Peek;
                if (c == '\\')
                {
                    _pos++;
                    if (AtEnd)
                    {
                        throw Error("A string was not closed.");
                    }

                    var escaped = Peek;
                    if (multiLine && (escaped == '\n' || escaped == '\r' || escaped == ' ' || escaped == '\t'))
                    {
                        // A line-ending backslash trims the whitespace that follows.
                        while (!AtEnd && (Peek == ' ' || Peek == '\t' || Peek == '\r' || Peek == '\n'))
                        {
                            if (Peek == '\n')
                            {
                                _line++;
                            }

                            _pos++;
                        }

                        continue;
                    }

                    _pos++;
                    switch (escaped)
                    {
                        case 'b': builder.Append('\b'); break;
                        case 't': builder.Append('\t'); break;
                        case 'n': builder.Append('\n'); break;
                        case 'f': builder.Append('\f'); break;
                        case 'r': builder.Append('\r'); break;
                        case '"': builder.Append('"'); break;
                        case '\\': builder.Append('\\'); break;
                        case 'u': builder.Append(ParseUnicodeEscape(4)); break;
                        case 'U': builder.Append(ParseUnicodeEscape(8)); break;
                        default: throw Error("Unknown escape '\\" + escaped + "'.");
                    }

                    continue;
                }

                if (c == '\n')
                {
                    if (!multiLine)
                    {
                        throw Error("A string was not closed.");
                    }

                    _line++;
                }
                else if (c == '\r' && !multiLine)
                {
                    throw Error("A string was not closed.");
                }

                builder.Append(c);
                _pos++;
            }
        }

        private string ParseUnicodeEscape(int digits)
        {
            if (_pos + digits > _text.Length)
            {
                throw Error("A unicode escape was cut short.");
            }

            var hex = _text.Substring(_pos, digits);
            _pos += digits;
            var codePoint = int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return char.ConvertFromUtf32(codePoint);
        }

        private string ParseLiteralString(bool multiLine)
        {
            var quote = multiLine ? "'''" : "'";
            ExpectText(quote);
            if (multiLine)
            {
                SkipOneNewline();
            }

            var start = _pos;
            while (true)
            {
                if (AtEnd)
                {
                    throw Error("A string was not closed.");
                }

                if (StartsWith(quote))
                {
                    var end = _pos;
                    _pos += quote.Length;
                    if (multiLine)
                    {
                        while (!AtEnd && Peek == '\'')
                        {
                            end++;
                            _pos++;
                        }
                    }

                    return _text.Substring(start, end - start);
                }

                if (Peek == '\n')
                {
                    if (!multiLine)
                    {
                        throw Error("A string was not closed.");
                    }

                    _line++;
                }

                _pos++;
            }
        }

        private void SkipOneNewline()
        {
            if (!AtEnd && Peek == '\r')
            {
                _pos++;
            }

            if (!AtEnd && Peek == '\n')
            {
                _pos++;
                _line++;
            }
        }

        private void SkipSpaces()
        {
            while (!AtEnd && (Peek == ' ' || Peek == '\t'))
            {
                _pos++;
            }
        }

        private void SkipWhitespaceAndComments(bool includeNewlines)
        {
            while (!AtEnd)
            {
                var c = Peek;
                if (c == ' ' || c == '\t')
                {
                    _pos++;
                }
                else if (c == '#')
                {
                    while (!AtEnd && Peek != '\n')
                    {
                        _pos++;
                    }
                }
                else if (includeNewlines && (c == '\r' || c == '\n'))
                {
                    if (c == '\n')
                    {
                        _line++;
                    }

                    _pos++;
                }
                else
                {
                    return;
                }
            }
        }

        private bool AtEnd => _pos >= _text.Length;

        private bool AtNewline => !AtEnd && (Peek == '\r' || Peek == '\n');

        private char Peek => _text[_pos];

        private bool StartsWith(string value)
        {
            return string.CompareOrdinal(_text, _pos, value, 0, value.Length) == 0 && _pos + value.Length <= _text.Length;
        }

        private void Expect(char c)
        {
            if (AtEnd || Peek != c)
            {
                throw Error("'" + c + "' was expected.");
            }

            _pos++;
        }

        private void ExpectText(string value)
        {
            if (!StartsWith(value))
            {
                throw Error("'" + value + "' was expected.");
            }

            _pos += value.Length;
        }

        private TomlParseException Error(string message)
        {
            return new TomlParseException(_line, message);
        }
    }
}
