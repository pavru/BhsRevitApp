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
/// it into a container is worth naming now, so that it is recognised later. Recording which module
/// types a host declared is not that: it is the second key a command in a feature's Entry assembly
/// has to be found by, because the assembly it sits in belongs to no edition.
/// </para>
/// </remarks>
public static class HostRegistry
{
    private static readonly ConcurrentDictionary<Guid, Entry> Hosts = new();

    /// <summary>Registers one edition's services under its own add-in id.</summary>
    /// <param name="addInId">The key; the same GUID as the manifest's.</param>
    /// <param name="owner">The edition's own assembly.</param>
    /// <param name="services">What the edition's commands and modules are handed.</param>
    /// <param name="modules">
    /// The types of the modules the edition declared - listed, not started. A module whose
    /// <c>Start</c> threw is still a feature the edition offers, and its commands still belong here;
    /// they will say what went wrong rather than claim the feature was never declared.
    /// </param>
    /// <remarks>
    /// Additive and idempotent, like everything else that lives in a shared AppDomain: registering
    /// again replaces only this key's entry, and never touches another edition's.
    /// </remarks>
    public static void Register(Guid addInId, Assembly owner, IFeatureServices services, IEnumerable<Type> modules)
    {
        if (addInId == Guid.Empty)
            throw new ArgumentException("An add-in id is required.", nameof(addInId));
        if (owner is null)
            throw new ArgumentNullException(nameof(owner));
        if (services is null)
            throw new ArgumentNullException(nameof(services));
        if (modules is null)
            throw new ArgumentNullException(nameof(modules));

        // Copied, so that what was declared at registration is what is answered later - not whatever
        // an edition's Modules property happens to return the next time somebody enumerates it.
        var declared = modules.Where(type => type is not null).Distinct().ToArray();

        Hosts[addInId] = new Entry(owner, services, declared);
    }

    /// <summary>Removes this edition's entry, and only this one.</summary>
    public static bool Unregister(Guid addInId) => Hosts.TryRemove(addInId, out _);

    /// <summary>The services for one add-in, or null if it never registered.</summary>
    public static IFeatureServices? Find(Guid addInId) =>
        Hosts.TryGetValue(addInId, out var entry) ? entry.Services : null;

    /// <summary>
    /// Every host with a user interface that declared <paramref name="feature"/> in its modules, by
    /// add-in id.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Only hosts with a user interface</b>, because only a command asks, and a command is reached
    /// by pressing something. A <c>DBApplication</c> add-in declaring the same feature is a module
    /// host, not a place a button can run - offering it here would turn a missing edition into a
    /// cast failure one line later.
    /// </para>
    /// <para>
    /// <b>The declared type exactly, not anything assignable to it.</b> A constraint of
    /// <c>IFeatureModule</c> on the caller would otherwise make <c>IFeatureModule</c> itself a
    /// wildcard matching every host, and a type argument that means "any edition" is a lookup that
    /// cannot fail and therefore cannot tell anybody anything.
    /// </para>
    /// <para>
    /// A snapshot: empty when nothing matches, and never null. Several entries is a development state
    /// - the owner's production rule is one edition installed - and the caller decides between them.
    /// </para>
    /// </remarks>
    public static IReadOnlyDictionary<Guid, IUiFeatureServices> FindByFeature(Type feature)
    {
        var found = new Dictionary<Guid, IUiFeatureServices>();

        if (feature is null)
            return found;

        foreach (var pair in Hosts)
        {
            if (pair.Value.Services is IUiFeatureServices ui && Array.IndexOf(pair.Value.Modules, feature) >= 0)
                found[pair.Key] = ui;
        }

        return found;
    }

    /// <summary>
    /// The services for whoever owns an assembly, as a fallback.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The primary key comes from <c>commandData.Application.ActiveAddInId</c>, which no measurement
    /// has isolated yet: the probe recorded the id of the host its command was handed, and this very
    /// fallback could have handed the same one. The raw value is recorded now. Commands built from a
    /// feature's Entry manifest do not come here - <see cref="FindByFeature"/> finds them.
    /// </para>
    /// <para>
    /// This remains the fallback of <c>CommandEntryPoint&lt;TCommand&gt;</c>, the entry point that sits
    /// in an edition's own assembly: the assembly a type came from is a second way in when the id
    /// answers nothing, or answers a host that has no user interface.
    /// </para>
    /// <para>
    /// <b>A host with a user interface wins over one without, and that is a correction.</b> One
    /// assembly can own both forms - the probe does, an <c>Application</c> and a
    /// <c>DBApplication</c> in one manifest - and the first version returned whichever the dictionary
    /// yielded first. Every caller narrows the answer to <see cref="IUiFeatureServices"/>, so getting
    /// the DB half read as "no host" for a host that was right there. Ties are broken by add-in id,
    /// so the answer does not depend on enumeration order either.
    /// </para>
    /// </remarks>
    public static IFeatureServices? FindByAssembly(Assembly assembly)
    {
        if (assembly is null)
            return null;

        IFeatureServices? withoutInterface = null;

        foreach (var pair in Hosts.OrderBy(pair => pair.Key))
        {
            if (!ReferenceEquals(pair.Value.Owner, assembly))
                continue;

            if (pair.Value.Services is IUiFeatureServices)
                return pair.Value.Services;

            withoutInterface ??= pair.Value.Services;
        }

        return withoutInterface;
    }

    /// <summary>How many editions are registered. For diagnostics, and for noticing there are two.</summary>
    public static int Count => Hosts.Count;

    private sealed class Entry
    {
        public Entry(Assembly owner, IFeatureServices services, Type[] modules)
        {
            Owner = owner;
            Services = services;
            Modules = modules;
        }

        public Assembly Owner { get; }

        public IFeatureServices Services { get; }

        /// <summary>The module types declared at registration, whether or not they started.</summary>
        public Type[] Modules { get; }
    }
}
