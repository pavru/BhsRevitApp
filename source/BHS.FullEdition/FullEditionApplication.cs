using BHS.MEP.Cabling.Declaration;
using BHS.Revit.Abstractions;
using BHS.Revit.Host;

namespace BHS.FullEdition;

/// <summary>
/// The full edition: everything this framework offers, in one add-in.
/// </summary>
/// <remarks>
/// <para>
/// <b>Who it is, what it offers, and where the buttons go - and that is the whole of it.</b> The base
/// handles settings, logging, the pump, the host registry, model settings and the ribbon; each feature
/// brings its own buttons and entry points in its Entry assembly; an edition says who it is, lists
/// its features and names the tab. Anything an edition has to write twice belongs in the base or in
/// the feature instead.
/// </para>
/// <para>
/// <b><see cref="AddInId"/> is the same GUID as the manifest's, and it is a key rather than a
/// label.</b> Revit files its trust and its isolation by it, and <c>HostRegistry</c> is keyed on it.
/// A mismatch would look exactly like an add-in that failed to start.
/// </para>
/// <para>
/// <b>A feature's command finds this host by the feature, not by this id.</b> Its entry point asks the
/// registry for the host with a user interface whose <see cref="Modules"/> list declared the feature -
/// with one edition installed, which is the production rule, that is this one and nothing else is
/// consulted. Only when two hosts declare the same feature, a development state, does the entry point
/// read <c>ActiveAddInId</c> to choose between them.
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

    /// <summary>The tab every feature's buttons go on in this edition.</summary>
    /// <remarks>
    /// <para>
    /// <b>The edition chooses the tab, the feature chooses the panel.</b> The cabling buttons name the
    /// panel <c>Cabling</c> and no tab, and land here.
    /// </para>
    /// <para>
    /// <b><c>BHS</c> because that is where the buttons were before they moved into the feature.</b> The
    /// id Revit gives an add-in button encodes its tab, its panel and its name, so keeping all three
    /// keeps the ids of the cabling commands what they were.
    /// </para>
    /// </remarks>
    protected override string? RibbonTab => "BHS";

    /// <summary>The features this edition declares.</summary>
    /// <remarks>
    /// <para>
    /// <b>A list rather than a scan, and the usual reason is not the real one here.</b> Scanning a
    /// folder means loading every assembly in it to ask whether it declares anything - true, and
    /// beside the point, because these would be loaded anyway. The real reason is that an edition is
    /// defined by what it declares and not by what happens to be in its folder: a reduced edition is
    /// this same tree with a shorter list, and under a scan it would differ from the full one only
    /// by the contents of a directory, which makes a bad deployment indistinguishable from a product.
    /// </para>
    /// <para>
    /// <b>The list does more than start modules.</b> The host builds a feature's buttons from
    /// <c>&lt;P&gt;.Entry.features.json</c> only when this list names a module from
    /// <c>&lt;P&gt;.Declaration</c>, and the commands behind those buttons find their host through the
    /// same list. Listed is what counts, not started: a module whose <c>Start</c> threw keeps its
    /// feature's buttons, and the commands can say what went wrong.
    /// </para>
    /// <para>
    /// <b>Nothing of the feature's own work is behind this.</b> A declaration assembly holds only
    /// what the application must know before anybody uses the feature - identifiers for the
    /// registrations Revit allows only while it starts, and the availability rule its buttons ask - and
    /// is forbidden by RVTDEC001 from referencing the assemblies that do the work. Those still load on
    /// a press and not before, with the entry points in the feature's Entry assembly that Revit loads
    /// earlier to ask availability - measured on all four releases on 2026-09-14, in the probe's Gate
    /// experiment, which is cut exactly like cabling.
    /// </para>
    /// </remarks>
    protected override IReadOnlyList<IFeatureModule> Modules { get; } = new IFeatureModule[]
    {
        new CablingFeature(),
    };
}
