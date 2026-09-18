using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace BimHouse.RefCheck;

/// <summary>One finding about a feature's Entry assembly, or about the add-in that ships one.</summary>
internal readonly record struct EntryFinding(string Code, string File, string Message);

/// <summary>
/// Checks that a feature's Entry assembly holds names and nothing else, and that an edition shipping one
/// has declared the feature it belongs to.
/// </summary>
/// <remarks>
/// <para>
/// <b>What an Entry assembly is for.</b> Revit resolves a button's <c>ClassName</c> and its
/// <c>AvailabilityClassName</c> inside the one assembly the button names - read from the IL of all four
/// releases, and measured as a <c>TypeLoadException</c> dialog - so a feature's command entry points and
/// its availability classes have to sit together, in <c>&lt;P&gt;.Entry</c>. The work is in the feature,
/// the rule in the declaration, the host lookup in the generic base in <c>BHS.Revit.Abstractions</c>. The
/// Entry classes are the names Revit can resolve, and nothing more.
/// </para>
/// <para>
/// <b>Why "nothing more" is checked and not trusted.</b> Everything this guards against is ordinary C#.
/// A method on an entry point compiles; a field compiles; a rule written in the feature assembly compiles,
/// because the Entry project references the feature. And each of them breaks something that only running
/// Revit shows: the Entry assembly is loaded as soon as the tab holding its buttons is shown, to construct
/// the availability class, so what it names is loaded then too; and Revit keeps one availability instance
/// per assembly path and class name for the whole session (IL of all four releases), so a field on one goes
/// stale. This repository has watched the one comparable mistake - <c>typeof</c> on a ribbon button - load
/// a feature while nothing complained, and has watched a written rule go unexecuted for months.
/// </para>
/// <list type="bullet">
/// <item><description><b>RVTENT001</b> - an Entry assembly declares something other than empty public
/// sealed classes deriving directly from <c>CommandEntryPoint`2</c>, <c>AvailabilityEntryPoint`1</c> or
/// <c>PaneEntryPoint</c> in <c>BHS.Revit.Abstractions</c> - an interface on one included.</description></item>
/// <item><description><b>RVTENT002</b> - an entry point in <c>&lt;P&gt;.Entry</c> names a rule that is not
/// in <c>&lt;P&gt;.Declaration</c> or in <c>BHS.Revit.Abstractions</c>, or a feature that is not a module
/// in <c>&lt;P&gt;.Declaration</c>, or has a type nested in either argument that would load another
/// assembly of ours.</description></item>
/// <item><description><b>RVTENT004</b> - an Application add-in ships a feature's Entry manifest and names
/// no module from that feature's declaration. <c>RVTENT003</c>, the tab in an Entry manifest, reads
/// JSON rather than metadata and lives in <c>RibbonCheck</c>.</description></item>
/// </list>
/// </remarks>
internal static class EntryCheck
{
    /// <summary>Assemblies named like this are a feature's Entry, by convention of the repository.</summary>
    public const string Suffix = ".Entry";

    private const string DeclarationSuffix = ".Declaration";
    private const string Abstractions = "BHS.Revit.Abstractions";
    private const string CommandBase = "BHS.Revit.Abstractions.CommandEntryPoint`2";
    private const string AvailabilityBase = "BHS.Revit.Abstractions.AvailabilityEntryPoint`1";

    /// <summary>
    /// The base of a button that toggles a dockable pane. Non-generic: the class's own name is what finds
    /// its pane, so it has no type argument that could load anything, and <c>RVTENT002</c> has nothing to
    /// ask of it. Whether a pane button's class derives from it is <c>RVTPAN004</c>'s question.
    /// </summary>
    private const string PaneBase = TypeFacts.PaneEntryPoint;

    private const string EmbeddedAttribute = "Microsoft.CodeAnalysis.EmbeddedAttribute";
    private const string CompilerGeneratedAttribute = "System.Runtime.CompilerServices.CompilerGeneratedAttribute";

    private const string ShapeCode = "RVTENT001";
    private const string ArgumentCode = "RVTENT002";
    private const string UndeclaredCode = "RVTENT004";

    private enum Kind
    {
        None,
        Command,
        Availability,
        Pane,
    }

    /// <summary>Checks every Entry assembly in a directory. Never throws.</summary>
    /// <param name="directory">The folder to look in; usually a project's output.</param>
    /// <param name="checkedFiles">How many Entry assemblies were found and read.</param>
    /// <param name="entryPoints">How many classes were checked across them.</param>
    public static IReadOnlyList<EntryFinding> Check(string directory, out int checkedFiles, out int entryPoints)
    {
        var findings = new List<EntryFinding>();
        checkedFiles = 0;
        entryPoints = 0;

        if (!Directory.Exists(directory))
            return findings;

        var references = Directory.GetFiles(directory, "*.dll");

        foreach (var file in EntryAssemblies(directory))
        {
            using var facts = TypeFacts.Open(file, references);

            // Not a managed assembly. The same answer DeclarationCheck gives, for the same reason: a file
            // that happens to carry the name is not what the convention is about.
            if (facts is null)
                continue;

            checkedFiles++;

            try
            {
                entryPoints += CheckAssembly(file, facts, findings);
            }
            catch (Exception error)
            {
                // A finding rather than a crash, and a finding rather than a skip: an Entry assembly this
                // tool cannot read to the end is one whose emptiness nobody has confirmed.
                findings.Add(new EntryFinding(ShapeCode, file,
                    $"the assembly could not be read to the end, so its entry points were not checked: {error.Message}"));
            }
        }

        return findings;
    }

    /// <summary>Every file in a directory named like a feature's Entry assembly, in a stable order.</summary>
    /// <remarks>
    /// By name alone. <see cref="Check"/> goes on to skip a file that is not a managed assembly; the
    /// question <c>RibbonCheck</c> asks of the same list - is its manifest beside it - is about the name.
    /// </remarks>
    public static IReadOnlyList<string> EntryAssemblies(string directory) =>
        Directory.Exists(directory)
            ? Directory.GetFiles(directory, "*" + Suffix + ".dll")
                .Where(path => Path.GetFileNameWithoutExtension(path).EndsWith(Suffix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];

    /// <summary>
    /// Checks that an Application add-in declares every feature whose Entry manifest lies beside it.
    /// Never throws.
    /// </summary>
    /// <param name="directory">The folder the add-in assembly is in.</param>
    /// <param name="applicationPath">The add-in assembly an Application manifest entry names.</param>
    /// <param name="entryManifests">How many Entry manifests were checked against it.</param>
    /// <remarks>
    /// <para>
    /// <b>A static stand-in for "listed in Modules", and knowingly weaker.</b> The host honours
    /// <c>&lt;P&gt;.Entry.features.json</c> only when the edition's <c>Modules</c> list holds a module
    /// type defined in <c>&lt;P&gt;.Declaration</c>, and skips it with an error otherwise. What metadata
    /// can show is that the add-in assembly names such a type somewhere - which the list does, since
    /// <c>new CablingFeature()</c> is a reference, and which a type named for any other reason also
    /// does. So a pass here means "could be listed", and the host's check at startup still decides;
    /// a failure here means the list cannot possibly hold it.
    /// </para>
    /// <para>
    /// A type argument or a <c>typeof</c> also count as naming the type: each leaves the same type
    /// reference in the metadata that <c>new</c> does.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<EntryFinding> CheckApplication(string directory, string applicationPath, out int entryManifests)
    {
        var findings = new List<EntryFinding>();
        entryManifests = 0;

        var manifests = RibbonCheck.Manifests(directory).Where(RibbonCheck.IsEntryManifest).ToList();

        if (manifests.Count == 0)
            return findings;

        using var facts = TypeFacts.Open(applicationPath, Directory.GetFiles(directory, "*.dll"));
        var application = Path.GetFileName(applicationPath);

        if (facts is null)
        {
            findings.Add(new EntryFinding(UndeclaredCode, applicationPath,
                $"'{application}' could not be read as an assembly, so no Entry manifest beside it could be " +
                "matched against its Modules list."));
            return findings;
        }

        foreach (var manifest in manifests)
        {
            entryManifests++;

            var fileName = Path.GetFileName(manifest);
            var feature = fileName.Substring(0, fileName.Length - RibbonCheck.EntryExtension.Length);
            var declaration = feature + DeclarationSuffix;
            List<TypeName> modules;

            try
            {
                modules = facts.ReferencedFrom(declaration)
                    .Where(type => facts.Implements(type, TypeFacts.FeatureModule))
                    .ToList();
            }
            catch (Exception error)
            {
                findings.Add(new EntryFinding(UndeclaredCode, applicationPath,
                    $"'{fileName}' could not be matched against '{application}', whose metadata could not be read " +
                    $"to the end: {error.Message}"));
                continue;
            }

            if (modules.Count > 0)
                continue;

            var besides = File.Exists(Path.Combine(directory, declaration + ".dll"))
                ? $"'{application}' names no IFeatureModule type from '{declaration}'"
                : $"'{declaration}.dll' is not even in this folder, so '{application}' cannot name a module from it";

            findings.Add(new EntryFinding(UndeclaredCode, applicationPath,
                $"'{fileName}' lies beside the Application add-in '{application}', and {besides}. The host " +
                $"builds that manifest's buttons only when the edition's Modules list declares a module from " +
                $"'{declaration}', and otherwise skips it with an error in the log - so this edition would ship " +
                $"the feature's assemblies and show none of its buttons. Add the feature's module to Modules, " +
                $"or stop referencing '{feature}{Suffix}'. Read from metadata: naming a module type anywhere " +
                "passes this check, and the host's check at startup is the one that decides."));
        }

        return findings;
    }

    /// <returns>How many classes were checked.</returns>
    private static int CheckAssembly(string file, TypeFacts facts, List<EntryFinding> findings)
    {
        var reader = facts.Reader;
        var checkedTypes = 0;

        foreach (var handle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(handle);

            if (IsModuleType(reader, handle, type) || IsEmbeddedByCompiler(facts, type))
                continue;

            checkedTypes++;

            var name = TypeFacts.DefinitionName(reader, type);
            var problems = new List<string>();

            Shape(type, problems);

            var baseType = facts.Decode(type.BaseType);
            var kind = KindOf(baseType);

            if (kind == Kind.None)
            {
                problems.Add(baseType is null
                    ? "it has no base class"
                    : $"it derives from {baseType.Display} in '{Or(baseType.Assembly, "no assembly")}', not directly " +
                      $"from CommandEntryPoint<TFeature, TCommand>, AvailabilityEntryPoint<TRule> or PaneEntryPoint in '{Abstractions}'");
            }

            Members(facts, type, baseType, problems);

            if (problems.Count > 0)
            {
                findings.Add(new EntryFinding(ShapeCode, file,
                    $"'{name}' is not an empty entry point: {string.Join("; ", problems)}. An Entry assembly " +
                    "holds only empty public sealed classes deriving from CommandEntryPoint<TFeature, TCommand>, " +
                    "AvailabilityEntryPoint<TRule> or PaneEntryPoint, because every class in it is a name Revit resolves and " +
                    "nothing else: Revit loads this assembly as soon as the tab holding its buttons is shown, so " +
                    "whatever runs here runs outside the feature's laziness, and it keeps one availability " +
                    "instance per class for the whole session, so whatever is kept here goes stale. Put the " +
                    "work in the feature's command and the decision in a rule in its declaration."));
            }

            if (baseType is not null && kind is Kind.Command or Kind.Availability)
                Arguments(file, name, kind, baseType, facts, findings);
        }

        return checkedTypes;
    }

    /// <summary>The first row of the type table, which every module has and nobody writes.</summary>
    private static bool IsModuleType(MetadataReader reader, TypeDefinitionHandle handle, TypeDefinition type) =>
        MetadataTokens.GetRowNumber(handle) == 1
        && reader.GetString(type.Name) == "<Module>"
        && type.Namespace.IsNil;

    /// <summary>
    /// The attribute types the compiler writes into an assembly when the framework lacks them.
    /// </summary>
    /// <remarks>
    /// Seen in the net48-revit2024 build of both Entry assemblies and in none of the .NET ones:
    /// <c>Microsoft.CodeAnalysis.EmbeddedAttribute</c>, <c>NullableAttribute</c> and
    /// <c>RefSafetyRulesAttribute</c>, each non-public and each carrying both
    /// <c>[CompilerGenerated]</c> and <c>[Embedded]</c> - the latter defined in the same assembly. The
    /// criterion is all three at once and never "anything non-public": a hand-written internal helper
    /// is exactly what this check is for.
    /// </remarks>
    private static bool IsEmbeddedByCompiler(TypeFacts facts, TypeDefinition type)
    {
        if ((type.Attributes & TypeAttributes.VisibilityMask) != TypeAttributes.NotPublic || type.IsNested)
            return false;

        var reader = facts.Reader;
        var embedded = false;
        var generated = false;

        foreach (var attributeHandle in type.GetCustomAttributes())
        {
            var attribute = reader.GetCustomAttribute(attributeHandle);

            if (attribute.Constructor.Kind == HandleKind.MethodDefinition)
            {
                var declaring = reader.GetTypeDefinition(
                    reader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType());

                embedded |= TypeFacts.DefinitionName(reader, declaring) == EmbeddedAttribute;
            }
            else if (attribute.Constructor.Kind == HandleKind.MemberReference)
            {
                var parent = facts.Decode(reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent);

                generated |= parent?.FullName == CompilerGeneratedAttribute;
            }
        }

        return embedded && generated;
    }

    private static void Shape(TypeDefinition type, List<string> problems)
    {
        var attributes = type.Attributes;

        if (type.IsNested)
            problems.Add("it is nested in another type");
        else if ((attributes & TypeAttributes.VisibilityMask) != TypeAttributes.Public)
            problems.Add("it is not public");

        if ((attributes & TypeAttributes.Interface) != 0)
            problems.Add("it is an interface");

        if ((attributes & TypeAttributes.Abstract) != 0)
            problems.Add("it is abstract");
        else if ((attributes & TypeAttributes.Sealed) == 0)
            problems.Add("it is not sealed");

        if (type.GetGenericParameters().Count > 0)
            problems.Add("it is generic, and Revit 2024 refuses a constructed generic name");
    }

    private static Kind KindOf(TypeName? baseType)
    {
        if (baseType is null || !string.Equals(baseType.Assembly, Abstractions, StringComparison.OrdinalIgnoreCase))
            return Kind.None;

        return baseType.FullName switch
        {
            CommandBase when baseType.Arguments.Count == 2 => Kind.Command,
            AvailabilityBase when baseType.Arguments.Count == 1 => Kind.Availability,
            PaneBase when baseType.Arguments.Count == 0 => Kind.Pane,
            _ => Kind.None
        };
    }

    /// <summary>
    /// Everything a class declares beyond the constructor the compiler writes for an empty one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The constructor is held to its body and not only to its signature, because "a parameterless
    /// constructor" is also where somebody would put the logic a method is forbidden to hold. The body
    /// the compiler writes for an empty class calls the base constructor and returns: <c>02 28 tok 2A</c>
    /// in Release, <c>02 28 tok 00 2A</c> in Debug, which adds one <c>nop</c> - both read off the probe's
    /// Entry build, with no locals and no exception regions. The token must name the base's own
    /// constructor.
    /// </para>
    /// <para>
    /// Public, because that is what the compiler gives an empty public class, so any other accessibility
    /// was written on purpose. How Revit constructs the class from its name - and so whether a non-public
    /// constructor would work - is not measured, and the check does not depend on it.
    /// </para>
    /// </remarks>
    private static void Members(TypeFacts facts, TypeDefinition type, TypeName? baseType, List<string> problems)
    {
        var reader = facts.Reader;

        foreach (var handle in type.GetFields())
            problems.Add($"a field '{reader.GetString(reader.GetFieldDefinition(handle).Name)}'");

        foreach (var handle in type.GetProperties())
            problems.Add($"a property '{reader.GetString(reader.GetPropertyDefinition(handle).Name)}'");

        foreach (var handle in type.GetEvents())
            problems.Add($"an event '{reader.GetString(reader.GetEventDefinition(handle).Name)}'");

        foreach (var handle in type.GetNestedTypes())
            problems.Add($"a nested type '{reader.GetString(reader.GetTypeDefinition(handle).Name)}'");

        // An interface is no member and no logic, and it loads all the same: constructing the class
        // resolves every interface it declares, so one from the feature assembly brings that assembly in
        // when Revit constructs the availability class on tab show. Reproduced on a throwaway Entry
        // assembly, where it passed every other part of this check. No false positive on the real ones:
        // C# does not repeat a base class's interfaces on the derived type, and both Entry assemblies in
        // the tree carry no interface row.
        foreach (var handle in type.GetInterfaceImplementations())
            problems.Add($"an interface '{facts.Decode(reader.GetInterfaceImplementation(handle).Interface)?.Display ?? "?"}'");

        foreach (var handle in type.GetMethods())
        {
            var method = reader.GetMethodDefinition(handle);
            var name = reader.GetString(method.Name);
            var isStatic = (method.Attributes & MethodAttributes.Static) != 0;

            if (name == ".cctor")
            {
                problems.Add("a static constructor");
                continue;
            }

            if (name != ".ctor" || isStatic)
            {
                // Accessors and explicit interface implementations are methods too, and each one is logic.
                problems.Add($"a method '{name}'");
                continue;
            }

            int parameters;

            try
            {
                parameters = method.DecodeSignature(ParameterCounter.Instance, null).ParameterTypes.Length;
            }
            catch (BadImageFormatException)
            {
                parameters = -1;
            }

            if (parameters != 0)
            {
                problems.Add("a constructor that takes parameters");
                continue;
            }

            if ((method.Attributes & MethodAttributes.MemberAccessMask) != MethodAttributes.Public)
                problems.Add("a constructor that is not public");

            if (!IsBaseCallOnly(facts, method, baseType))
                problems.Add("a constructor that does more than call its base");
        }
    }

    private static bool IsBaseCallOnly(TypeFacts facts, MethodDefinition method, TypeName? baseType)
    {
        if (method.RelativeVirtualAddress == 0 || baseType is null)
            return false;

        MethodBodyBlock body;

        try
        {
            body = facts.Image.GetMethodBody(method.RelativeVirtualAddress);
        }
        catch (BadImageFormatException)
        {
            return false;
        }

        if (!body.LocalSignature.IsNil || body.ExceptionRegions.Length != 0)
            return false;

        var il = body.GetILBytes();

        if (il is null)
            return false;

        // ldarg.0; call <token>; [nop;] ret
        var shaped = il.Length is 7 or 8
                     && il[0] == 0x02
                     && il[1] == 0x28
                     && il[il.Length - 1] == 0x2A
                     && (il.Length == 7 || il[6] == 0x00);

        if (!shaped)
            return false;

        var token = BitConverter.ToInt32(il, 2);
        var called = MetadataTokens.EntityHandle(token);

        if (called.Kind != HandleKind.MemberReference)
            return false;

        var reference = facts.Reader.GetMemberReference((MemberReferenceHandle)called);

        if (facts.Reader.GetString(reference.Name) != ".ctor")
            return false;

        var parent = facts.Decode(reference.Parent);

        return parent is not null
               && string.Equals(parent.Assembly, baseType.Assembly, StringComparison.OrdinalIgnoreCase)
               && parent.Display == baseType.Display;
    }

    /// <remarks>
    /// <para>
    /// <b>The declaration of this Entry's own feature, not any declaration.</b> The expected assembly is
    /// derived from the Entry assembly's name, <c>&lt;P&gt;.Entry</c> to <c>&lt;P&gt;.Declaration</c>, because
    /// that is the pair the host matches: it builds <c>&lt;P&gt;.Entry.features.json</c> only when the
    /// edition lists a module from <c>&lt;P&gt;.Declaration</c>. The first version accepted any assembly
    /// ending in <c>.Declaration</c>, and a command naming another feature's module then passed here and
    /// passed <c>RVTENT004</c>, had its button built, and refused on the press because the edition listed
    /// the other feature. Reasoned from the code, then shown red by pointing the probe's Entry command at
    /// <c>CablingFeature</c>; both Entry assemblies in the tree already meet the stricter rule.
    /// </para>
    /// <para>
    /// <b>Nested type arguments count too.</b> Loading the class loads its base instantiation, and that
    /// resolves every type in the arguments however deep - so <c>AvailabilityEntryPoint&lt;DocRule&lt;T&gt;&gt;</c>
    /// with <c>T</c> from the feature loads the feature when the tab is shown, which the top-level
    /// question alone passed. Reproduced on a throwaway set of projects. A nested argument is flagged
    /// only when its assembly lies in the folder being checked, which is what keeps framework types -
    /// never copied there - out of it; and it may come from the expected declaration or from what a
    /// declaration is itself allowed to name, since either is loaded at startup already.
    /// </para>
    /// </remarks>
    private static void Arguments(
        string file,
        string name,
        Kind kind,
        TypeName baseType,
        TypeFacts facts,
        List<EntryFinding> findings)
    {
        var expected = ExpectedDeclaration(file, facts);
        var top = baseType.Arguments[0];

        if (kind == Kind.Availability)
        {
            if (!SameAssembly(top.Assembly, expected) && !SameAssembly(top.Assembly, Abstractions))
            {
                findings.Add(new EntryFinding(ArgumentCode, file,
                    $"'{name}' wraps the rule {top.Display} from '{Or(top.Assembly, "no assembly")}'. A rule in " +
                    $"this Entry assembly belongs in '{expected}' - the declaration of the feature it belongs " +
                    $"to - or in '{Abstractions}', because both are loaded at startup anyway: Revit constructs " +
                    "this class when the tab holding its buttons is shown, and a rule anywhere else loads its " +
                    $"assembly right then, before anybody pressed anything. Move the rule to '{expected}'."));
            }

            NestedArguments(file, name, top, expected, facts, findings);
            return;
        }

        if (!SameAssembly(top.Assembly, expected))
        {
            findings.Add(new EntryFinding(ArgumentCode, file,
                $"'{name}' finds its host by {top.Display} from '{Or(top.Assembly, "no assembly")}', and the " +
                $"feature of this Entry assembly is declared in '{expected}'. The host builds this Entry " +
                $"manifest's buttons only when the edition lists a module from '{expected}', and the command " +
                "finds its host by exactly the feature type named here - so a feature from any other assembly " +
                "is a button that is built and then answers \"this edition does not declare the feature\", or " +
                "an assembly loaded at startup for nothing."));
        }
        else if (!facts.Implements(top, TypeFacts.FeatureModule))
        {
            findings.Add(new EntryFinding(ArgumentCode, file,
                $"'{name}' finds its host by {top.Display}, which does not implement IFeatureModule - or " +
                $"'{top.Assembly}.dll' is not beside it to say. Only a module can be listed in an " +
                "edition's Modules list, and the entry point finds its host through that list, so this " +
                "command would never find one."));
        }

        NestedArguments(file, name, top, expected, facts, findings);
    }

    /// <summary>
    /// Flags a type inside the arguments of <paramref name="top"/> that would load an assembly of ours
    /// beyond the ones startup has loaded already.
    /// </summary>
    private static void NestedArguments(
        string file,
        string name,
        TypeName top,
        string expected,
        TypeFacts facts,
        List<EntryFinding> findings)
    {
        foreach (var nested in Nested(top))
        {
            if (nested.Assembly.Length == 0 || !facts.IsBeside(nested.Assembly))
                continue;

            if (SameAssembly(nested.Assembly, expected) || DeclarationCheck.MayName(nested.Assembly))
                continue;

            findings.Add(new EntryFinding(ArgumentCode, file,
                $"'{name}' names {nested.Display} from '{nested.Assembly}' inside the type argument " +
                $"{top.Display}. Loading this class loads its base with every type in its arguments, nested " +
                $"ones included, so '{nested.Assembly}' comes in when Revit constructs the class - for an " +
                "availability class, when the tab holding its buttons is shown. A type argument here, at any " +
                $"depth, may come from '{expected}' or from what a declaration may itself name."));
        }
    }

    /// <summary>Every type argument below <paramref name="type"/>, at every depth.</summary>
    private static IEnumerable<TypeName> Nested(TypeName type) =>
        type.Arguments.SelectMany(argument => Nested(argument).Prepend(argument));

    /// <summary><c>&lt;P&gt;.Declaration</c> for the Entry assembly <c>&lt;P&gt;.Entry</c>.</summary>
    private static string ExpectedDeclaration(string file, TypeFacts facts)
    {
        var entry = facts.AssemblyName.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase)
            ? facts.AssemblyName
            : Path.GetFileNameWithoutExtension(file);

        return entry.Substring(0, entry.Length - Suffix.Length) + DeclarationSuffix;
    }

    private static bool SameAssembly(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static string Or(string value, string fallback) => string.IsNullOrEmpty(value) ? fallback : value;
}
