using System.Collections.Concurrent;
using System.Reflection;

namespace BHS.Revit.Abstractions;

/// <summary>
/// Where a type Revit constructed finds the host it belongs to.
/// </summary>
/// <remarks>
/// <para>
/// One irreducible border makes this necessary. Revit builds commands, availability classes and
/// updaters itself, from two strings - an assembly and a class name - so their constructors cannot
/// be given anything. No container would remove that; a container would solve it with a locator
/// too, just a hidden one. So the locator is written down, kept to one question, and named.
/// </para>
/// <para>
/// <b>Keyed by <c>AddInId</c>, and that is the whole design.</b> The previous generation kept a
/// static <c>Host</c> instead, and on Revit 2024 that is a trap: every add-in lives in one
/// AppDomain, so the static inside this assembly is shared between our own editions, and two of
/// them means the second silently wins - commands of the first then resolve services out of the
/// second. Keyed, each edition registers under its own key and neither can displace the other.
/// The same reasoning already shaped <c>LogRouter.Default</c>.
/// </para>
/// <para>
/// It lives in the contract assembly rather than the host on purpose: a command must find its host
/// without referencing it, so the meeting place has to be something both sides already share.
/// </para>
/// <para>
/// <b>What it does not do:</b> resolve types, build graphs, know lifetimes. The temptation to grow
/// it into a container is worth naming now, so that it is recognised later.
/// </para>
/// </remarks>
public static class HostRegistry
{
    private static readonly ConcurrentDictionary<Guid, Entry> Hosts = new();

    /// <summary>Registers one edition's services under its own add-in id.</summary>
    /// <remarks>
    /// Additive and idempotent, like everything else that lives in a shared AppDomain: registering
    /// again replaces only this key's entry, and never touches another edition's.
    /// </remarks>
    public static void Register(Guid addInId, Assembly owner, IFeatureServices services)
    {
        if (addInId == Guid.Empty)
            throw new ArgumentException("An add-in id is required.", nameof(addInId));
        if (owner is null)
            throw new ArgumentNullException(nameof(owner));
        if (services is null)
            throw new ArgumentNullException(nameof(services));

        Hosts[addInId] = new Entry(owner, services);
    }

    /// <summary>Removes this edition's entry, and only this one.</summary>
    public static bool Unregister(Guid addInId) => Hosts.TryRemove(addInId, out _);

    /// <summary>The services for one add-in, or null if it never registered.</summary>
    public static IFeatureServices? Find(Guid addInId) =>
        Hosts.TryGetValue(addInId, out var entry) ? entry.Services : null;

    /// <summary>
    /// The services for whoever owns an assembly, as a fallback.
    /// </summary>
    /// <remarks>
    /// The primary key comes from <c>commandData.Application.ActiveAddInId</c>, which is documented
    /// as the add-in currently executing but has not been measured from inside a command on any
    /// release. Until it has, standing on it alone would be standing on a document rather than on a
    /// measurement, so the assembly a type came from is kept as a second way in.
    /// </remarks>
    public static IFeatureServices? FindByAssembly(Assembly assembly)
    {
        if (assembly is null)
            return null;

        foreach (var entry in Hosts.Values)
        {
            if (ReferenceEquals(entry.Owner, assembly))
                return entry.Services;
        }

        return null;
    }

    /// <summary>How many editions are registered. For diagnostics, and for noticing there are two.</summary>
    public static int Count => Hosts.Count;

    private sealed class Entry
    {
        public Entry(Assembly owner, IFeatureServices services)
        {
            Owner = owner;
            Services = services;
        }

        public Assembly Owner { get; }

        public IFeatureServices Services { get; }
    }
}
