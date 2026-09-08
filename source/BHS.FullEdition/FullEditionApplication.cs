using BHS.Revit.Abstractions;
using BHS.Revit.Host;

namespace BHS.FullEdition;

/// <summary>
/// The full edition: everything this framework offers, in one add-in.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two members, and that is the whole of it.</b> The base handles settings, logging, the pump,
/// the host registry, model settings and the ribbon; an edition says who it is. Anything an edition
/// has to write twice belongs in the base instead.
/// </para>
/// <para>
/// <b><see cref="AddInId"/> is the same GUID as the manifest's, and it is a key rather than a
/// label.</b> Revit files its trust and its isolation by it, <c>HostRegistry</c> is keyed on it, and
/// a command created by Revit finds its services through <c>ActiveAddInId</c>. A mismatch would look
/// exactly like an add-in that failed to start.
/// </para>
/// <para>
/// The name says "full" because reduced editions are expected - the same framework with fewer
/// features - and each will be its own add-in with its own id. That is why nothing here is static.
/// </para>
/// </remarks>
public sealed class FullEditionApplication : RevitAddInApplication
{
    /// <summary>The identity in <c>BHS.FullEdition.addin</c>. Never changes.</summary>
    public static readonly Guid Id = new("3f8b1d64-9c27-4a5e-8d13-6e0a72c4f5b1");

    protected override Guid AddInId => Id;

    protected override string Name => "BHS.FullEdition";
}
