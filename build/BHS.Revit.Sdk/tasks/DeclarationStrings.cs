using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml;

namespace BHS.Revit.Sdk;

/// <summary>
///     The strings a feature's declaration shows before the feature loads - button texts, tooltips, pane
///     titles - read from one neutral <c>.resx</c> and the culture files beside it.
/// </summary>
/// <remarks>
///     <para>
///     <b>Why baked into json and not read from resources at run time.</b> These strings are needed while
///     the ribbon and the panes are being built, before anything of the feature is loaded - and a
///     <c>ResourceManager</c> over the feature's assembly would load it right there, the same failure a
///     <c>typeof</c> on a button is. So the source stays a <c>.resx</c>, where the project's rule wants
///     every interface string, and the SDK renders it into the manifest: resolved text in the neutral
///     file, one overlay per culture beside it.
///     </para>
///     <para>
///     <b>Read with <see cref="XmlDocument"/>, not <c>ResXResourceReader</c>.</b> The reader lives in
///     System.Windows.Forms, which an MSBuild task on .NET has no business loading; a <c>.resx</c> is plain
///     XML, and only its <c>data</c> elements without a <c>type</c> - strings - are wanted.
///     </para>
/// </remarks>
internal sealed class DeclarationStrings
{
    private DeclarationStrings(string path, Dictionary<string, string> values)
    {
        Path = path;
        Values = values;
    }

    public string Path { get; }

    /// <summary>Key to text. Keys compare ordinally: a key is an identifier, and case is part of it.</summary>
    public Dictionary<string, string> Values { get; }

    public static DeclarationStrings Read(string path)
    {
        var document = new XmlDocument { XmlResolver = null };

        using (var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit }))
            document.Load(reader);

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var root = document.DocumentElement ?? throw new InvalidDataException("the file has no root element");

        foreach (XmlNode node in root.SelectNodes("data")!)
        {
            if (node is not XmlElement data)
                continue;

            // Anything with a type is a serialised object, not a string; a mimetype is a blob.
            if (data.HasAttribute("type") || data.HasAttribute("mimetype"))
                continue;

            var name = data.GetAttribute("name");

            if (name.Length == 0)
                continue;

            if (values.ContainsKey(name))
                throw new InvalidDataException($"the key '{name}' is defined twice");

            values[name] = data.SelectSingleNode("value")?.InnerText ?? string.Empty;
        }

        return new DeclarationStrings(path, values);
    }

    /// <summary>
    ///     The culture files beside a neutral file: <c>&lt;name&gt;.&lt;culture&gt;.resx</c>, culture by culture,
    ///     in a stable order.
    /// </summary>
    /// <remarks>
    ///     A middle segment that is not a culture name is not one of these, and is left alone - the same
    ///     rule the base SDK applies when it decides whether a <c>.resx</c> becomes a satellite.
    /// </remarks>
    public static IReadOnlyList<KeyValuePair<string, string>> CulturesBeside(string neutralPath)
    {
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(neutralPath)) ?? ".";
        var name = System.IO.Path.GetFileNameWithoutExtension(neutralPath);
        var found = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in Directory.GetFiles(directory, name + ".*.resx"))
        {
            var middle = System.IO.Path.GetFileNameWithoutExtension(file).Substring(name.Length + 1);

            if (middle.Length == 0 || middle.IndexOf('.') >= 0 || !IsCulture(middle))
                continue;

            found[middle] = file;
        }

        return new List<KeyValuePair<string, string>>(found);
    }

    private static bool IsCulture(string name)
    {
        try
        {
            return CultureInfo.GetCultureInfo(name) is { } culture && !culture.Equals(CultureInfo.InvariantCulture);
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
    }
}
