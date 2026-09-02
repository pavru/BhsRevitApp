using System.Collections.Immutable;
using System.Reflection.Metadata;

namespace BimHouse.RefCheck;

/// <summary>
/// Signature decoder used only to count parameters. Every type in a signature
/// collapses to the same placeholder, which is all the arity comparison needs.
/// </summary>
internal sealed class ParameterCounter : ISignatureTypeProvider<object, object?>
{
    public static readonly ParameterCounter Instance = new();

    private static readonly object Placeholder = new();

    private ParameterCounter()
    {
    }

    public object GetArrayType(object elementType, ArrayShape shape) => Placeholder;

    public object GetByReferenceType(object elementType) => Placeholder;

    public object GetFunctionPointerType(MethodSignature<object> signature) => Placeholder;

    public object GetGenericInstantiation(object genericType, ImmutableArray<object> typeArguments) => Placeholder;

    public object GetGenericMethodParameter(object? genericContext, int index) => Placeholder;

    public object GetGenericTypeParameter(object? genericContext, int index) => Placeholder;

    public object GetModifiedType(object modifier, object unmodifiedType, bool isRequired) => Placeholder;

    public object GetPinnedType(object elementType) => Placeholder;

    public object GetPointerType(object elementType) => Placeholder;

    public object GetPrimitiveType(PrimitiveTypeCode typeCode) => Placeholder;

    public object GetSZArrayType(object elementType) => Placeholder;

    public object GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => Placeholder;

    public object GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => Placeholder;

    public object GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) => Placeholder;
}
