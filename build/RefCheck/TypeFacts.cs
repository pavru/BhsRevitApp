using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace BimHouse.RefCheck;

/// <summary>
///     A type as a signature names it: the assembly that defines it, its full name, and the type
///     arguments it was constructed with.
/// </summary>
/// <param name="Assembly">
///     The simple name of the defining assembly. Empty for a type no assembly on disk defines - a
///     primitive, or anything the decoder could not name.
/// </param>
/// <param name="FullName">Namespace and name, nested types joined with '+', generic arity kept (<c>`2</c>).</param>
/// <param name="Arguments">The type arguments, in order; empty for a type that is not a generic instance.</param>
internal sealed record TypeName(string Assembly, string FullName, IReadOnlyList<TypeName> Arguments)
{
    /// <summary>What a person reads: <c>CommandEntryPoint&lt;CablingFeature, RouteCablingCommand&gt;</c>.</summary>
    public string Display
    {
        get
        {
            var tick = FullName.IndexOf('`');
            var name = tick < 0 ? FullName : FullName.Substring(0, tick);

            return Arguments.Count == 0 ? name : name + "<" + string.Join(", ", Arguments.Select(a => a.Display)) + ">";
        }
    }
}

/// <summary>
///     Answers questions about types in a built assembly, without loading it.
/// </summary>
/// <remarks>
///     <para>
///     Loading is not an option: these assemblies are compiled against the Revit API, which is not on
///     MSBuild's probing path and would not run there anyway. Metadata is read straight from the file.
///     </para>
///     <para>
///     It exists for checks the C# compiler cannot make. The first two were modal dialogs in front of a
///     person before they were build errors: an availability class that lives in another assembly, and
///     a command entry point with no <c>[Transaction]</c> on it. The Entry checks came after them and
///     ask about the shape of what is legal C# - an entry point that grew a method, a rule that lives in
///     the assembly the rule exists to keep unloaded - which is why this reads type arguments and not
///     only the definition a generic base was built from.
///     </para>
/// </remarks>
internal sealed class TypeFacts : IDisposable
{
    public const string ExternalCommand = "Autodesk.Revit.UI.IExternalCommand";
    public const string ExternalCommandAvailability = "Autodesk.Revit.UI.IExternalCommandAvailability";
    public const string TransactionAttribute = "Autodesk.Revit.Attributes.TransactionAttribute";
    public const string FeatureModule = "BHS.Revit.Abstractions.IFeatureModule";

    private readonly Dictionary<string, PEReader?> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _paths = new(StringComparer.OrdinalIgnoreCase);
    private readonly PEReader _image;
    private readonly MetadataReader _target;

    private TypeFacts(PEReader target, IEnumerable<string> referencePaths)
    {
        _image = target;
        _target = target.GetMetadataReader();
        AssemblyName = AssemblyNameOf(_target);
        _byName[string.Empty] = target;

        foreach (var path in referencePaths)
        {
            var name = Path.GetFileNameWithoutExtension(path);

            if (!string.IsNullOrEmpty(name) && !_paths.ContainsKey(name))
                _paths[name] = path;
        }
    }

    /// <summary>Opens an assembly, or returns null when it cannot be read at all.</summary>
    public static TypeFacts? Open(string assemblyPath, IEnumerable<string> referencePaths)
    {
        try
        {
            // Prefetched so that the file is read once and not held open a moment longer than the
            // task needs it - this is the assembly the build has just written. The whole image rather
            // than the metadata alone, because the Entry check reads constructor bodies, and with only
            // the metadata prefetched the stream is closed and every method body answers "PE image not
            // available". These are our own assemblies, a few kilobytes each.
            var reader = new PEReader(File.OpenRead(assemblyPath),
                PEStreamOptions.PrefetchMetadata | PEStreamOptions.PrefetchEntireImage);

            // Read once here so that a file that is not a managed assembly fails now, not later.
            _ = reader.GetMetadataReader();

            return new TypeFacts(reader, referencePaths);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The simple name of the target assembly.</summary>
    public string AssemblyName { get; }

    /// <summary>The target's metadata, for checks that walk its tables themselves.</summary>
    public MetadataReader Reader => _target;

    /// <summary>The target's image, for checks that read a method body.</summary>
    public PEReader Image => _image;

    /// <summary>Whether the target assembly declares this type itself.</summary>
    public bool Declares(string fullName) => Find(_target, fullName) is not null;

    /// <summary>
    ///     Whether an assembly of this simple name is the target or one of the reference paths it was
    ///     opened with - for the checks, the folder being checked.
    /// </summary>
    public bool IsBeside(string simpleName) =>
        string.Equals(simpleName, AssemblyName, StringComparison.OrdinalIgnoreCase) || _paths.ContainsKey(simpleName);

    /// <summary>
    ///     Names a type the target refers to - a base type, a type argument, the parent of a member
    ///     reference - with its defining assembly and its type arguments. Null when the handle names
    ///     nothing a type name can describe.
    /// </summary>
    public TypeName? Decode(EntityHandle handle)
    {
        if (handle.IsNil)
            return null;

        try
        {
            return handle.Kind switch
            {
                HandleKind.TypeDefinition => Names.Instance.GetTypeFromDefinition(_target, (TypeDefinitionHandle)handle, 0),
                HandleKind.TypeReference => Names.Instance.GetTypeFromReference(_target, (TypeReferenceHandle)handle, 0),
                HandleKind.TypeSpecification => _target.GetTypeSpecification((TypeSpecificationHandle)handle)
                    .DecodeSignature(Names.Instance, (object?)null),
                _ => null
            };
        }
        catch (BadImageFormatException)
        {
            return null;
        }
    }

    /// <summary>
    ///     Whether a named type implements an interface, however far up the chain - for a type defined in
    ///     the target or in any assembly beside it.
    /// </summary>
    /// <remarks>False when the defining assembly is not beside the target, or does not declare the type.</remarks>
    public bool Implements(TypeName type, string interfaceFullName)
    {
        var reader = string.Equals(type.Assembly, AssemblyName, StringComparison.OrdinalIgnoreCase)
            ? _target
            : type.Assembly.Length == 0 ? null : Load(type.Assembly);

        if (reader is null)
            return false;

        var handle = Find(reader, type.FullName);

        return handle is not null && Implements(reader, handle.Value, interfaceFullName, depth: 0);
    }

    /// <summary>
    ///     Every type the target names from one other assembly, by simple name.
    /// </summary>
    /// <remarks>
    ///     A type reference is in the metadata only when something in the assembly uses the type - the
    ///     compiler writes no reference for a <c>using</c> or for a project reference nobody touches - so
    ///     this answers "what does this assembly actually name from there", not "what could it".
    /// </remarks>
    public IReadOnlyList<TypeName> ReferencedFrom(string assemblySimpleName)
    {
        var found = new List<TypeName>();

        foreach (var handle in _target.TypeReferences)
        {
            var name = Decode(handle);

            if (name is not null && string.Equals(name.Assembly, assemblySimpleName, StringComparison.OrdinalIgnoreCase))
                found.Add(name);
        }

        return found;
    }

    /// <summary>Whether a type declared here implements an interface, however far up the chain.</summary>
    public bool Implements(string fullName, string interfaceFullName)
    {
        var handle = Find(_target, fullName);

        return handle is not null && Implements(_target, handle.Value, interfaceFullName, depth: 0);
    }

    /// <summary>
    ///     Whether the attribute is declared on the type itself.
    /// </summary>
    /// <remarks>
    ///     Directly, never up the base chain - and that is a deliberately stricter rule than the CLR's.
    ///     Measured: <c>TransactionAttribute</c> is <c>[AttributeUsage(AttributeTargets.Class)]</c> with
    ///     no named arguments on any of the four releases, so <c>Inherited</c> defaults to <c>true</c>
    ///     and a base could in principle carry it. Whether Revit asks with <c>inherit: true</c> has not
    ///     been measured. The check does not depend on the answer: the transaction mode is a property
    ///     of each command, so each command states it, and a mode inherited from somewhere else would
    ///     be a mode nobody chose.
    /// </remarks>
    public bool HasAttribute(string fullName, string attributeFullName)
    {
        var handle = Find(_target, fullName);

        if (handle is null)
            return false;

        foreach (var attributeHandle in _target.GetTypeDefinition(handle.Value).GetCustomAttributes())
        {
            if (AttributeName(_target, _target.GetCustomAttribute(attributeHandle)) == attributeFullName)
                return true;
        }

        return false;
    }

    public void Dispose()
    {
        foreach (var reader in _byName.Values)
            reader?.Dispose();
    }

    private bool Implements(MetadataReader reader, TypeDefinitionHandle handle, string interfaceFullName, int depth)
    {
        // A chain this long is a corrupt file or a cycle, and either way the answer is no.
        if (depth > 16)
            return false;

        var type = reader.GetTypeDefinition(handle);

        foreach (var implementation in type.GetInterfaceImplementations())
        {
            var name = Name(reader, reader.GetInterfaceImplementation(implementation).Interface);

            if (name == interfaceFullName)
                return true;
        }

        // Up the base chain, across assemblies. The whole point of the arrangement being checked is
        // that the entry point in the edition derives from a generic base in the framework, which is
        // where IExternalCommand is actually implemented.
        var (baseReader, baseHandle) = Resolve(reader, type.BaseType);

        return baseReader is not null
               && baseHandle is not null
               && Implements(baseReader, baseHandle.Value, interfaceFullName, depth + 1);
    }

    private (MetadataReader?, TypeDefinitionHandle?) Resolve(MetadataReader reader, EntityHandle handle)
    {
        if (handle.IsNil)
            return (null, null);

        switch (handle.Kind)
        {
            case HandleKind.TypeDefinition:
                return (reader, (TypeDefinitionHandle)handle);

            case HandleKind.TypeReference:
            {
                var reference = reader.GetTypeReference((TypeReferenceHandle)handle);

                if (reference.ResolutionScope.Kind != HandleKind.AssemblyReference)
                    return (null, null);

                var scope = reader.GetAssemblyReference((AssemblyReferenceHandle)reference.ResolutionScope);
                var other = Load(reader.GetString(scope.Name));

                if (other is null)
                    return (null, null);

                var full = FullName(reader.GetString(reference.Namespace), reader.GetString(reference.Name));
                var found = Find(other, full);

                return found is null ? (null, null) : (other, found);
            }

            case HandleKind.TypeSpecification:
            {
                // A generic base - CommandEntryPoint<FooCommand>. What matters is the definition it
                // was constructed from, so the signature is decoded down to that and no further.
                var specification = reader.GetTypeSpecification((TypeSpecificationHandle)handle);
                var underlying = specification.DecodeSignature(new DefinitionOf(), (object?)null);

                return underlying.IsNil ? (null, null) : Resolve(reader, underlying);
            }

            default:
                return (null, null);
        }
    }

    private MetadataReader? Load(string simpleName)
    {
        if (_byName.TryGetValue(simpleName, out var cached))
            return cached?.GetMetadataReader();

        PEReader? reader = null;

        if (_paths.TryGetValue(simpleName, out var path))
        {
            try
            {
                reader = new PEReader(File.OpenRead(path), PEStreamOptions.PrefetchMetadata);
                _ = reader.GetMetadataReader();
            }
            catch (Exception)
            {
                reader?.Dispose();
                reader = null;
            }
        }

        _byName[simpleName] = reader;
        return reader?.GetMetadataReader();
    }

    /// <remarks>
    ///     By the definition's full nested name, the same spelling <see cref="Names"/> gives a reference:
    ///     <c>NS.Outer+Inner</c>. A nested definition keeps an empty namespace row of its own, so comparing
    ///     namespace and name alone found nothing for it - and a legal module nested in a static class in a
    ///     declaration failed <c>RVTENT002</c> and <c>RVTENT004</c> as "not an IFeatureModule", reproduced on
    ///     a throwaway set of projects. For a top-level type the spelling is what it always was.
    ///     <para>
    ///     Walking a base chain through a nested <em>base</em> is still not supported: <c>Resolve</c> gives up
    ///     on a type reference scoped to another type reference. Nothing in the tree derives from one.
    ///     </para>
    /// </remarks>
    private static TypeDefinitionHandle? Find(MetadataReader reader, string fullName)
    {
        foreach (var handle in reader.TypeDefinitions)
        {
            if (DefinitionName(reader, reader.GetTypeDefinition(handle)) == fullName)
                return handle;
        }

        return null;
    }

    private static string Name(MetadataReader reader, EntityHandle handle) => handle.Kind switch
    {
        HandleKind.TypeDefinition => FullNameOf(reader, reader.GetTypeDefinition((TypeDefinitionHandle)handle)),
        HandleKind.TypeReference => FullNameOf(reader, reader.GetTypeReference((TypeReferenceHandle)handle)),
        _ => string.Empty
    };

    private static string FullNameOf(MetadataReader reader, TypeDefinition type) =>
        FullName(reader.GetString(type.Namespace), reader.GetString(type.Name));

    private static string FullNameOf(MetadataReader reader, TypeReference type) =>
        FullName(reader.GetString(type.Namespace), reader.GetString(type.Name));

    private static string FullName(string @namespace, string name) =>
        string.IsNullOrEmpty(@namespace) ? name : @namespace + "." + name;

    private static string AttributeName(MetadataReader reader, CustomAttribute attribute)
    {
        var constructor = attribute.Constructor;

        return constructor.Kind switch
        {
            HandleKind.MethodDefinition =>
                Name(reader, reader.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType()),
            HandleKind.MemberReference =>
                Name(reader, reader.GetMemberReference((MemberReferenceHandle)constructor).Parent),
            _ => string.Empty
        };
    }

    private static string AssemblyNameOf(MetadataReader reader) =>
        reader.IsAssembly ? reader.GetString(reader.GetAssemblyDefinition().Name) : string.Empty;

    /// <summary>
    ///     Decodes a type signature into a <see cref="TypeName"/>, keeping the type arguments.
    /// </summary>
    /// <remarks>
    ///     The Entry checks need what <see cref="DefinitionOf"/> throws away: an entry point is only as
    ///     good as the feature and the rule it names, and both are type arguments of its base. Anything
    ///     that cannot be a type argument an entry point would use - arrays, pointers, generic parameters
    ///     - decodes to null, and a null argument becomes a name in no assembly, which fails every check
    ///     that asks where a type is defined.
    /// </remarks>
    private sealed class Names : ISignatureTypeProvider<TypeName?, object?>
    {
        public static readonly Names Instance = new();

        private static readonly IReadOnlyList<TypeName> None = Array.Empty<TypeName>();

        public TypeName? GetGenericInstantiation(TypeName? genericType, ImmutableArray<TypeName?> typeArguments)
        {
            if (genericType is null)
                return null;

            var arguments = new List<TypeName>(typeArguments.Length);

            foreach (var argument in typeArguments)
                arguments.Add(argument ?? new TypeName(string.Empty, "?", None));

            return genericType with { Arguments = arguments };
        }

        public TypeName? GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
            new(AssemblyNameOf(reader), DefinitionName(reader, reader.GetTypeDefinition(handle)), None);

        public TypeName? GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            var reference = reader.GetTypeReference(handle);
            var name = reader.GetString(reference.Name);
            var scope = reference.ResolutionScope;

            switch (scope.Kind)
            {
                case HandleKind.AssemblyReference:
                    return new TypeName(
                        reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)scope).Name),
                        FullName(reader.GetString(reference.Namespace), name),
                        None);

                case HandleKind.TypeReference:
                {
                    // Nested: named after its outer type, and defined wherever that one is.
                    var outer = GetTypeFromReference(reader, (TypeReferenceHandle)scope, rawTypeKind);

                    return outer is null ? null : new TypeName(outer.Assembly, outer.FullName + "+" + name, None);
                }

                default:
                    // A module of this same assembly, or no scope at all: defined here.
                    return new TypeName(AssemblyNameOf(reader), FullName(reader.GetString(reference.Namespace), name), None);
            }
        }

        public TypeName? GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
            reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

        public TypeName? GetPrimitiveType(PrimitiveTypeCode typeCode) => new(string.Empty, "System." + typeCode, None);

        public TypeName? GetModifiedType(TypeName? modifier, TypeName? unmodifiedType, bool isRequired) => unmodifiedType;

        public TypeName? GetArrayType(TypeName? elementType, ArrayShape shape) => null;
        public TypeName? GetByReferenceType(TypeName? elementType) => null;
        public TypeName? GetFunctionPointerType(MethodSignature<TypeName?> signature) => null;
        public TypeName? GetGenericMethodParameter(object? genericContext, int index) => null;
        public TypeName? GetGenericTypeParameter(object? genericContext, int index) => null;
        public TypeName? GetPinnedType(TypeName? elementType) => null;
        public TypeName? GetPointerType(TypeName? elementType) => null;
        public TypeName? GetSZArrayType(TypeName? elementType) => null;
    }

    /// <summary>A type definition's full name, nested types joined with '+'.</summary>
    public static string DefinitionName(MetadataReader reader, TypeDefinition type)
    {
        var name = reader.GetString(type.Name);

        return type.IsNested
            ? DefinitionName(reader, reader.GetTypeDefinition(type.GetDeclaringType())) + "+" + name
            : FullName(reader.GetString(type.Namespace), name);
    }

    /// <summary>Decodes a type signature down to the definition a generic instance came from.</summary>
    private sealed class DefinitionOf : ISignatureTypeProvider<EntityHandle, object?>
    {
        public EntityHandle GetGenericInstantiation(EntityHandle genericType, ImmutableArray<EntityHandle> arguments) =>
            genericType;

        public EntityHandle GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
            handle;

        public EntityHandle GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) =>
            handle;

        public EntityHandle GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
            handle;

        public EntityHandle GetArrayType(EntityHandle elementType, ArrayShape shape) => default;
        public EntityHandle GetByReferenceType(EntityHandle elementType) => default;
        public EntityHandle GetFunctionPointerType(MethodSignature<EntityHandle> signature) => default;
        public EntityHandle GetGenericMethodParameter(object? genericContext, int index) => default;
        public EntityHandle GetGenericTypeParameter(object? genericContext, int index) => default;
        public EntityHandle GetModifiedType(EntityHandle modifier, EntityHandle unmodifiedType, bool isRequired) => unmodifiedType;
        public EntityHandle GetPinnedType(EntityHandle elementType) => default;
        public EntityHandle GetPointerType(EntityHandle elementType) => default;
        public EntityHandle GetPrimitiveType(PrimitiveTypeCode typeCode) => default;
        public EntityHandle GetSZArrayType(EntityHandle elementType) => default;
    }
}
