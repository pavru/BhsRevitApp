using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace BimHouse.RefCheck;

/// <summary>
///     Answers questions about types in a built assembly, without loading it.
/// </summary>
/// <remarks>
///     <para>
///     Loading is not an option: these assemblies are compiled against the Revit API, which is not on
///     MSBuild's probing path and would not run there anyway. Metadata is read straight from the file.
///     </para>
///     <para>
///     It exists for two checks the C# compiler cannot make, and both of them were modal dialogs in
///     front of a person before they were build errors: an availability class that lives in another
///     assembly, and a command entry point with no <c>[Transaction]</c> on it.
///     </para>
/// </remarks>
internal sealed class TypeFacts : IDisposable
{
    public const string ExternalCommand = "Autodesk.Revit.UI.IExternalCommand";
    public const string ExternalCommandAvailability = "Autodesk.Revit.UI.IExternalCommandAvailability";
    public const string TransactionAttribute = "Autodesk.Revit.Attributes.TransactionAttribute";

    private readonly Dictionary<string, PEReader?> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _paths = new(StringComparer.OrdinalIgnoreCase);
    private readonly MetadataReader _target;

    private TypeFacts(PEReader target, IEnumerable<string> referencePaths)
    {
        _target = target.GetMetadataReader();
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
            // task needs it - this is the assembly the build has just written.
            var reader = new PEReader(File.OpenRead(assemblyPath), PEStreamOptions.PrefetchMetadata);

            // Read once here so that a file that is not a managed assembly fails now, not later.
            _ = reader.GetMetadataReader();

            return new TypeFacts(reader, referencePaths);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Whether the target assembly declares this type itself.</summary>
    public bool Declares(string fullName) => Find(_target, fullName) is not null;

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
    ///     Directly, and never inherited: Revit reads <c>[Transaction]</c> off the type it constructs,
    ///     which is the one named on the button. A base class carrying it is not the question being
    ///     asked here, and answering a different question is how a check stops catching anything.
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

    private static TypeDefinitionHandle? Find(MetadataReader reader, string fullName)
    {
        foreach (var handle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(handle);

            if (FullName(reader.GetString(type.Namespace), reader.GetString(type.Name)) == fullName)
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
