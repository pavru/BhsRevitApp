using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace BimHouse.RefCheck;

/// <summary>A member the scanned assembly asks another assembly to provide.</summary>
/// <param name="Assembly">Simple name of the assembly the member is expected to live in.</param>
/// <param name="Type">Full type name, nested types joined with '+'.</param>
/// <param name="Member">Member name; ".ctor" for constructors.</param>
/// <param name="Arity">Parameter count for methods, -1 for fields.</param>
internal sealed record MemberUse(string Assembly, string Type, string Member, int Arity)
{
    /// <summary>Key including arity, matching <see cref="AssemblyIndex.Members"/>.</summary>
    public string Key => Arity < 0 ? $"{Type}::{Member}/field" : $"{Type}::{Member}/{Arity}";

    /// <summary>Key without arity, matching <see cref="AssemblyIndex.MemberNames"/>.</summary>
    public string NameKey => $"{Type}::{Member}";

    public override string ToString() => $"{Type}::{Member}" + (Arity < 0 ? "" : $"({Arity} args)");
}

/// <summary>The surface one assembly declares, reduced to what a member reference can name.</summary>
internal sealed class AssemblyIndex
{
    public required string Name { get; init; }

    public required string AssemblyVersion { get; init; }

    public required string FileVersion { get; init; }

    public required string Vendor { get; init; }

    /// <summary>Full type names, including those only forwarded to another assembly.</summary>
    public required HashSet<string> Types { get; init; }

    /// <summary>
    /// True for .NET Framework facade assemblies, which define nothing and only forward types
    /// elsewhere. A reference to one never binds to the copy in the Revit folder, so it cannot conflict.
    /// </summary>
    public required bool IsTypeForwarderOnly { get; init; }

    /// <summary>"Type::Member/arity" for methods, "Type::Member/field" for fields.</summary>
    public required HashSet<string> Members { get; init; }

    /// <summary>"Type::Member" without arity, so a missing overload reads differently from a missing member.</summary>
    public required HashSet<string> MemberNames { get; init; }

    /// <summary>
    /// Indexes the public and protected surface of a managed assembly. Returns null for
    /// native images and anything else without a managed metadata table.
    /// </summary>
    public static AssemblyIndex? Read(string path)
    {
        using var stream = File.OpenRead(path);
        using var peReader = new PEReader(stream);
        if (!peReader.HasMetadata) return null;

        var reader = peReader.GetMetadataReader();
        if (!reader.IsAssembly) return null;

        var definition = reader.GetAssemblyDefinition();
        var types = new HashSet<string>(StringComparer.Ordinal);
        var members = new HashSet<string>(StringComparer.Ordinal);
        var memberNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var handle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(handle);
            var typeName = FullNameOf(reader, type);
            if (IsCompilerGenerated(typeName)) continue;

            types.Add(typeName);

            foreach (var methodHandle in type.GetMethods())
            {
                var method = reader.GetMethodDefinition(methodHandle);
                var access = method.Attributes & MethodAttributes.MemberAccessMask;
                if (access is not (MethodAttributes.Public or MethodAttributes.Family)) continue;

                var name = reader.GetString(method.Name);
                if (IsCompilerGenerated(name)) continue;

                int arity;
                try
                {
                    arity = method.DecodeSignature(ParameterCounter.Instance, null).ParameterTypes.Length;
                }
                catch (BadImageFormatException)
                {
                    continue;
                }

                members.Add($"{typeName}::{name}/{arity}");
                memberNames.Add($"{typeName}::{name}");
            }

            foreach (var fieldHandle in type.GetFields())
            {
                var field = reader.GetFieldDefinition(fieldHandle);
                var access = field.Attributes & FieldAttributes.FieldAccessMask;
                if (access is not (FieldAttributes.Public or FieldAttributes.Family)) continue;

                var name = reader.GetString(field.Name);
                if (IsCompilerGenerated(name)) continue;

                members.Add($"{typeName}::{name}/field");
                memberNames.Add($"{typeName}::{name}");
            }
        }

        var declaredTypes = types.Count;

        // A forwarded type still resolves at runtime, so it must not read as missing.
        var forwardedTypes = 0;
        foreach (var handle in reader.ExportedTypes)
        {
            var exported = reader.GetExportedType(handle);
            var ns = reader.GetString(exported.Namespace);
            var name = reader.GetString(exported.Name);
            types.Add(string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}");
            forwardedTypes++;
        }

        return new AssemblyIndex
        {
            Name = reader.GetString(definition.Name),
            AssemblyVersion = definition.Version.ToString(),
            FileVersion = FileVersionOf(path),
            Vendor = ReadCompany(reader) ?? "",
            IsTypeForwarderOnly = declaredTypes == 0 && forwardedTypes > 0,
            Types = types,
            Members = members,
            MemberNames = memberNames
        };
    }

    /// <summary>Lists every member the assembly at <paramref name="path"/> expects other assemblies to provide.</summary>
    public static IReadOnlyList<MemberUse> ReadReferences(string path, out IReadOnlyDictionary<string, string> referencedVersions)
    {
        var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        referencedVersions = versions;

        using var stream = File.OpenRead(path);
        using var peReader = new PEReader(stream);
        if (!peReader.HasMetadata) return [];

        var reader = peReader.GetMetadataReader();

        var assemblyRefs = new Dictionary<int, string>();
        foreach (var handle in reader.AssemblyReferences)
        {
            var reference = reader.GetAssemblyReference(handle);
            var name = reader.GetString(reference.Name);
            assemblyRefs[MetadataTokens.GetToken(handle)] = name;
            versions[name] = reference.Version?.ToString() ?? "";
        }

        var typeRefs = new Dictionary<int, (string Type, string Assembly)>();
        foreach (var handle in reader.TypeReferences)
        {
            typeRefs[MetadataTokens.GetToken(handle)] = ResolveTypeReference(reader, handle, assemblyRefs);
        }

        var uses = new List<MemberUse>();
        foreach (var handle in reader.MemberReferences)
        {
            var member = reader.GetMemberReference(handle);
            if (member.Parent.Kind != HandleKind.TypeReference) continue;
            if (!typeRefs.TryGetValue(MetadataTokens.GetToken(member.Parent), out var owner)) continue;
            if (owner.Assembly.Length == 0) continue;

            var arity = -1;
            if (member.GetKind() == MemberReferenceKind.Method)
            {
                try
                {
                    arity = member.DecodeMethodSignature(ParameterCounter.Instance, null).ParameterTypes.Length;
                }
                catch (BadImageFormatException)
                {
                    continue;
                }
            }

            uses.Add(new MemberUse(owner.Assembly, owner.Type, reader.GetString(member.Name), arity));
        }

        return uses.Distinct().ToList();
    }

    private static (string Type, string Assembly) ResolveTypeReference(
        MetadataReader reader,
        TypeReferenceHandle handle,
        Dictionary<int, string> assemblyRefs)
    {
        var reference = reader.GetTypeReference(handle);
        var name = reader.GetString(reference.Name);
        var ns = reader.GetString(reference.Namespace);
        var scope = reference.ResolutionScope;

        if (scope.Kind == HandleKind.TypeReference)
        {
            var (outerType, outerAssembly) = ResolveTypeReference(reader, (TypeReferenceHandle)scope, assemblyRefs);
            return ($"{outerType}+{name}", outerAssembly);
        }

        var fullName = string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
        if (scope.Kind == HandleKind.AssemblyReference &&
            assemblyRefs.TryGetValue(MetadataTokens.GetToken(scope), out var assembly))
        {
            return (fullName, assembly);
        }

        return (fullName, "");
    }

    private static bool IsCompilerGenerated(string name) => name.Contains('<') || name.Contains('>');

    private static string FullNameOf(MetadataReader reader, TypeDefinition type)
    {
        var name = reader.GetString(type.Name);
        if (!type.IsNested)
        {
            var ns = reader.GetString(type.Namespace);
            return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
        }

        var declaring = reader.GetTypeDefinition(type.GetDeclaringType());
        return $"{FullNameOf(reader, declaring)}+{name}";
    }

    private static string FileVersionOf(string path)
    {
        var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
        return info.FileVersion ?? "";
    }

    /// <summary>
    /// Reads AssemblyCompanyAttribute so the collector can tell vendor assemblies apart
    /// from third-party ones without hard-coding a list of names.
    /// </summary>
    private static string? ReadCompany(MetadataReader reader)
    {
        foreach (var handle in reader.GetAssemblyDefinition().GetCustomAttributes())
        {
            var attribute = reader.GetCustomAttribute(handle);
            if (NameOfAttributeType(reader, attribute) != "AssemblyCompanyAttribute") continue;

            var blob = reader.GetBlobReader(attribute.Value);
            if (blob.Length < 2 || blob.ReadUInt16() != 1) continue;

            try
            {
                return blob.ReadSerializedString();
            }
            catch (BadImageFormatException)
            {
                return null;
            }
        }

        return null;
    }

    private static string? NameOfAttributeType(MetadataReader reader, CustomAttribute attribute)
    {
        switch (attribute.Constructor.Kind)
        {
            case HandleKind.MemberReference:
                var member = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
                if (member.Parent.Kind != HandleKind.TypeReference) return null;
                return reader.GetString(reader.GetTypeReference((TypeReferenceHandle)member.Parent).Name);

            case HandleKind.MethodDefinition:
                var method = reader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor);
                return reader.GetString(reader.GetTypeDefinition(method.GetDeclaringType()).Name);

            default:
                return null;
        }
    }
}
