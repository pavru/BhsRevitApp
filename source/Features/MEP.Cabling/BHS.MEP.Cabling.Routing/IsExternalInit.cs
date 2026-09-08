#if NETFRAMEWORK

// ReSharper disable once CheckNamespace
namespace System.Runtime.CompilerServices;

/// <summary>
/// Lets <c>init</c> accessors compile on .NET Framework, where the framework does not declare this.
/// </summary>
/// <remarks>
/// <para>
/// The SDK raises LangVersion to latest on every Revit target framework, so the syntax is available
/// on Revit 2024 - but syntax is all it opens. Records, <c>init</c>, indices and ranges each need a
/// type the framework does not have, and the compiler asks for it by name.
/// </para>
/// <para>
/// <b>Declared here rather than taken from a package, and the metric says why.</b> What counts on
/// net48 is not megabytes but assemblies somebody else may also ship, into an AppDomain every add-in
/// shares. A type declared inside our own assembly adds no reference and cannot collide; PolySharp
/// or a polyfill package would trade five lines for a dependency that has to be justified on all
/// four releases.
/// </para>
/// </remarks>
internal static class IsExternalInit;

#endif
