using System.Globalization;
using System.Text;

namespace BHS.Settings;

/// <summary>
/// Reads a settings file into the flat form configuration is kept in.
/// </summary>
/// <remarks>
/// Written by hand rather than taken from <c>Microsoft.Extensions.Configuration.Json</c>, and the
/// reason is two measurements. On <c>net48</c> that package brings seventeen assemblies, nine of
/// them BCL polyfills - <c>System.Text.Json</c>, <c>System.Text.Encodings.Web</c>,
/// <c>System.IO.Pipelines</c>, <c>Microsoft.Bcl.AsyncInterfaces</c> and the rest - against four
/// polyfills for the whole transport. And every assembly we ship into Revit is one another vendor
/// may ship too: on Revit 2026, DynamoForRevit loads
/// <c>Microsoft.Extensions.Configuration.Abstractions</c> 2.0.0.0 from Revit's own installation
/// directory before any add-in runs, and a later request for 10.0.0.0 fails outright rather than
/// quietly binding low. Twenty-two other copies of that one assembly are installed for 2026 on the
/// machine this was measured on.
/// <para>
/// So this assembly has no package references at all, and the format still has to be one people can
/// edit. That leaves reading it ourselves, which for a flat string store is a short job.
/// </para>
/// <para>
/// The output matches what the stock JSON provider produces: nested objects join with a colon,
/// arrays are indexed by ordinal, numbers and booleans keep their literal text, and an explicit
/// null - or an empty object - is stored as a null value rather than dropped, which is how a
/// deeper layer un-sets what a shallower one said. Comments and trailing commas are accepted for
/// the same reason the stock provider accepts them: these files are written by people.
/// </para>
/// </remarks>
public static class JsonSettings
{
    /// <summary>Reads one settings file from disk.</summary>
    public static IDictionary<string, string?> Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        return Parse(reader.ReadToEnd());
    }

    /// <summary>Reads settings text that has already been loaded.</summary>
    public static IDictionary<string, string?> Parse(string text)
    {
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var reader = new Reader(text);

        reader.SkipTrivia();

        if (reader.AtEnd)
            return data;

        if (reader.Peek() != '{')
            throw reader.Error("The top level of a settings file must be an object.");

        reader.ReadObject(string.Empty, data);
        reader.SkipTrivia();

        if (!reader.AtEnd)
            throw reader.Error("Unexpected content after the top-level object.");

        return data;
    }

    private sealed class Reader
    {
        private const char ByteOrderMark = '﻿';

        private readonly string _text;
        private int _index;

        public Reader(string text)
        {
            // A file written by Notepad starts with a byte order mark that survives decoding.
            _text = text.Length > 0 && text[0] == ByteOrderMark ? text.Substring(1) : text;
        }

        public bool AtEnd => _index >= _text.Length;

        public char Peek() => _text[_index];

        public FormatException Error(string message)
        {
            var line = 1;
            var column = 1;

            for (var i = 0; i < _index && i < _text.Length; i++)
            {
                if (_text[i] == '\n')
                {
                    line++;
                    column = 1;
                }
                else
                {
                    column++;
                }
            }

            return new FormatException(string.Format(
                CultureInfo.InvariantCulture, "{0} (line {1}, column {2})", message, line, column));
        }

        public void SkipTrivia()
        {
            while (_index < _text.Length)
            {
                var c = _text[_index];

                if (char.IsWhiteSpace(c))
                {
                    _index++;
                }
                else if (c == '/' && _index + 1 < _text.Length && _text[_index + 1] == '/')
                {
                    while (_index < _text.Length && _text[_index] != '\n')
                        _index++;
                }
                else if (c == '/' && _index + 1 < _text.Length && _text[_index + 1] == '*')
                {
                    _index += 2;
                    while (_index + 1 < _text.Length && !(_text[_index] == '*' && _text[_index + 1] == '/'))
                        _index++;

                    if (_index + 1 >= _text.Length)
                        throw Error("A block comment is not closed.");

                    _index += 2;
                }
                else
                {
                    return;
                }
            }
        }

        public void ReadObject(string path, IDictionary<string, string?> data)
        {
            Expect('{');
            SkipTrivia();

            var empty = true;

            while (!AtEnd && Peek() != '}')
            {
                empty = false;

                if (Peek() != '"')
                    throw Error("A member name must be a quoted string.");

                var name = ReadString();
                SkipTrivia();
                Expect(':');
                SkipTrivia();

                var child = path.Length == 0 ? name : path + ":" + name;
                ReadValue(child, data);
                SkipTrivia();

                if (AtEnd)
                    break;

                if (Peek() == ',')
                {
                    _index++;
                    SkipTrivia();
                }
                else if (Peek() != '}')
                {
                    throw Error("Expected a comma or the end of the object.");
                }
            }

            if (AtEnd)
                throw Error("An object is not closed.");

            _index++;

            // An empty object is not nothing: it is how a later layer clears what an earlier one
            // set, and the stock provider records it the same way.
            if (empty && path.Length > 0)
                Store(path, null, data);
        }

        private void ReadArray(string path, IDictionary<string, string?> data)
        {
            Expect('[');
            SkipTrivia();

            var index = 0;

            while (!AtEnd && Peek() != ']')
            {
                var element = index.ToString(CultureInfo.InvariantCulture);
                ReadValue(path.Length == 0 ? element : path + ":" + element, data);

                index++;
                SkipTrivia();

                if (AtEnd)
                    break;

                if (Peek() == ',')
                {
                    _index++;
                    SkipTrivia();
                }
                else if (Peek() != ']')
                {
                    throw Error("Expected a comma or the end of the array.");
                }
            }

            if (AtEnd)
                throw Error("An array is not closed.");

            _index++;

            if (index == 0 && path.Length > 0)
                Store(path, null, data);
        }

        private void ReadValue(string path, IDictionary<string, string?> data)
        {
            if (AtEnd)
                throw Error("A value was expected.");

            switch (Peek())
            {
                case '{':
                    ReadObject(path, data);
                    break;

                case '[':
                    ReadArray(path, data);
                    break;

                case '"':
                    Store(path, ReadString(), data);
                    break;

                default:
                    Store(path, ReadLiteral(), data);
                    break;
            }
        }

        /// <summary>
        /// Numbers, <c>true</c>, <c>false</c> and <c>null</c>, all kept as they were written.
        /// </summary>
        /// <remarks>
        /// No attempt to turn a number into a number: configuration is a string store, and the
        /// consumer decides what a value means. Keeping the literal is also what stops <c>1.50</c>
        /// from quietly becoming <c>1.5</c> in a value somebody reads as text.
        /// </remarks>
        private string? ReadLiteral()
        {
            var start = _index;

            while (_index < _text.Length && !IsDelimiter(_text[_index]))
                _index++;

            var literal = _text.Substring(start, _index - start);

            if (literal.Length == 0)
                throw Error("A value was expected.");

            if (string.Equals(literal, "null", StringComparison.Ordinal))
                return null;

            if (string.Equals(literal, "true", StringComparison.Ordinal) ||
                string.Equals(literal, "false", StringComparison.Ordinal))
                return literal;

            if (!double.TryParse(literal, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                throw Error($"'{literal}' is not a value. Text has to be quoted.");

            return literal;
        }

        private static bool IsDelimiter(char c) =>
            char.IsWhiteSpace(c) || c == ',' || c == '}' || c == ']' || c == '/';

        private string ReadString()
        {
            Expect('"');

            var builder = new StringBuilder();

            while (true)
            {
                if (AtEnd)
                    throw Error("A string is not closed.");

                var c = _text[_index++];

                if (c == '"')
                    return builder.ToString();

                if (c != '\\')
                {
                    builder.Append(c);
                    continue;
                }

                if (AtEnd)
                    throw Error("A string is not closed.");

                var escape = _text[_index++];

                switch (escape)
                {
                    case '"': builder.Append('"'); break;
                    case '\\': builder.Append('\\'); break;
                    case '/': builder.Append('/'); break;
                    case 'b': builder.Append('\b'); break;
                    case 'f': builder.Append('\f'); break;
                    case 'n': builder.Append('\n'); break;
                    case 'r': builder.Append('\r'); break;
                    case 't': builder.Append('\t'); break;

                    case 'u':
                        if (_index + 4 > _text.Length)
                            throw Error("An escape is cut short.");

                        // Surrogate pairs need no special handling: both halves arrive as their own
                        // escape, and appending each in turn rebuilds the character.
                        builder.Append((char)ushort.Parse(
                            _text.Substring(_index, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        _index += 4;
                        break;

                    default:
                        _index -= 2;
                        throw Error($"'\\{escape}' is not an escape.");
                }
            }
        }

        private void Expect(char c)
        {
            if (AtEnd || _text[_index] != c)
                throw Error($"'{c}' was expected.");

            _index++;
        }

        /// <summary>
        /// Records one value, refusing a key that was already given.
        /// </summary>
        /// <remarks>
        /// Refusing rather than overwriting, because within one file a repeated key is a mistake
        /// every time - and because the case-insensitive comparison means <c>Timeout</c> and
        /// <c>timeout</c> in the same file would otherwise collide in silence. Between files it is
        /// not a mistake at all: that is what the layers are for.
        /// </remarks>
        private void Store(string path, string? value, IDictionary<string, string?> data)
        {
            if (data.ContainsKey(path))
                throw Error($"'{path}' is set twice in the same file.");

            data[path] = value;
        }
    }
}
