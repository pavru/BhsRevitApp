using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using BHS.Logging;
using BHS.Revit.Abstractions;

namespace BHS.MEP.Cabling.Declaration;

/// <summary>
/// What an application has to know about cabling before anybody uses it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists at all.</b> A <c>FailureDefinition</c> can only be created while Revit is
/// starting - the API says so itself: <i>"this function cannot be used after Revit has already
/// started. Throws InvalidOperationException if invoked after Revit start-up is completed"</i> - and
/// the identifier it creates is wanted much later, in a command Revit constructs from a string when
/// somebody presses a button. Those two halves have to agree on the same identifier, and until this
/// assembly existed there was nowhere to put it: the feature's own assemblies load on a press, which
/// is too late, and the framework is not allowed to know what a cable is.
/// </para>
/// <para>
/// <b>Why the identifiers are here and not in the manifest.</b> The ribbon is declared in the
/// manifest because Revit reads it as strings and nothing of ours needs to agree with it. These are
/// the other case: <b>two of our own sides have to name the same thing</b> - whoever registers at
/// startup and whoever posts the message on a press. In a manifest the GUID would be a literal in a
/// .csproj and a second literal in code, with nothing to make them match.
/// </para>
/// <para>
/// <b>What must never appear in this file.</b> Anything that names the feature's own assemblies. This
/// type is constructed during <c>OnStartup</c> for every user of every edition that declares cabling,
/// used or not, so whatever it mentions is loaded then too - and on Revit 2024 an assembly that loads
/// holds its simple name in the AppDomain shared with every other vendor for the rest of the session.
/// The project references nothing that could be mentioned, so the mistake does not compile; RVTDEC001
/// checks the built assembly as well, because this repository has watched a written rule lapse.
/// </para>
/// <para>
/// <b>Severity is <c>Warning</c> for all four, and that is a decision rather than a default.</b> An
/// <c>Error</c> rolls the transaction back, and every one of these describes work that should happen
/// and be visible afterwards: a circuit the router could not reach is still a circuit the rest of the
/// run served. A warning stays in the model's warning list, which is where a reviewer looks.
/// </para>
/// <para>
/// <b>The registered string is only a default.</b> <c>FailureMessage.SetMessageString</c> exists on
/// all four releases, so each posting can name the circuit it is about rather than saying "some
/// circuits". What is registered here is what Revit shows when nobody says anything better.
/// </para>
/// <para>
/// <b>Known debt:</b> the strings below are English literals in the neutral position - the one that
/// becomes a resource key when declaration strings move into <c>.resx</c>. They are read by a person
/// in Revit's own warning window, so they are interface text and belong in resources; the mechanism
/// for baking declaration strings per culture does not exist yet, and a Russian literal here could
/// not become a key without being translated back first.
/// </para>
/// </remarks>
public sealed class CablingFeature : IFeatureModule
{
    /// <summary>A device with no cable tray or conduit within reach of it.</summary>
    /// <remarks>
    /// The most valuable of the four, measured: on the first real model this condition found a
    /// consumer of an adjacent discipline that nobody had routed carriers to - in either discipline.
    /// It is a fault in the model rather than a choice about how to wire, and it reads differently
    /// from the rest for that reason.
    /// </remarks>
    public static readonly FailureDefinitionId NoCarrierNear =
        new(new Guid("2a7a9ac1-3cb9-499d-a354-0d61c9352bd9"));

    /// <summary>A circuit whose devices cannot be reached through the carrier network.</summary>
    /// <remarks>
    /// Carriers exist near both ends and do not join up: the trays or conduits between them are
    /// missing or do not meet. Distinct from <see cref="NoCarrierNear"/>, where nothing is near at all.
    /// </remarks>
    public static readonly FailureDefinitionId NoConnectivity =
        new(new Guid("c328f8f0-7178-4acf-9f09-ddb60baf7ce8"));

    /// <summary>A circuit whose connection type was set to something nobody can read.</summary>
    /// <remarks>
    /// The run continues on the project default rather than refusing, and says so. Silence here would
    /// hide a typo for a month; refusal would stop a whole model over one parameter.
    /// </remarks>
    public static readonly FailureDefinitionId ConnectionUnreadable =
        new(new Guid("f9488721-fb61-4561-bdbd-016d19cbb221"));

    /// <summary>An element whose type calls it a junction box, joined to no carrier at all.</summary>
    /// <remarks>
    /// A box drawn beside a run instead of in it. From the routing side it looks like a box the
    /// calculation ignored for no reason, which is why it is named rather than dropped.
    /// </remarks>
    public static readonly FailureDefinitionId JunctionBoxJoinedToNothing =
        new(new Guid("06532413-0a11-49f2-b561-037b6975aa6d"));

    private static readonly (FailureDefinitionId Id, string Message)[] Declared =
    {
        (NoCarrierNear,
            "This device has no cable tray or conduit within reach, so no route to it could be found."),
        (NoConnectivity,
            "This circuit's devices cannot be reached through the cable trays and conduits of this model."),
        (ConnectionUnreadable,
            "This circuit's cable connection type could not be read, so the project default was used."),
        (JunctionBoxJoinedToNothing,
            "This element's type calls it a junction box, but it is not joined to any cable tray or conduit."),
    };

    public void Start(IFeatureServices services)
    {
        // Asked of Revit before anything is created, and this is the check rather than bookkeeping of
        // our own: the registry is one per session, it survives our Stop, and it sees other vendors -
        // a static of ours would see only us. On Revit 2024 two of our editions share one AppDomain
        // and both run this, so the second one always finds what the first registered.
        //
        // It is not optional politeness either: a duplicate id throws ArgumentException, per the API's
        // own documentation.
        //
        // Static, and measured rather than read: the reference XML lists this member on
        // ControlledApplication without saying so, and the first version of this line went through
        // services.Revit.Controlled. The compiler refused it on all four releases alike - CS0176 - so
        // the registry needs no instance of Revit at all. Named through ControlledApplication and not
        // through Application because that is the type both host forms have.
        var registry = ControlledApplication.GetFailureDefinitionRegistry();

        // Nothing has been created yet, so this is still recoverable and worth saying plainly. The
        // contract for a module permits a late start - "otherwise the first time something in it is
        // used" - and a late start here cannot work at all: the registry locks when Revit finishes
        // starting, and creation throws InvalidOperationException afterwards. Better a sentence
        // naming the reason than a stack trace naming the line.
        if (services.Revit.IsInitialized)
        {
            services.Log.Error(
                "cabling failure definitions were not registered: Revit has finished starting, and its " +
                "failure definition registry is locked after that. This module has to be started from " +
                "OnStartup - see the edition's Modules list.");
            return;
        }

        var created = 0;

        foreach (var (id, message) in Declared)
        {
            if (registry.FindFailureDefinition(id) is not null)
                continue;

            FailureDefinition.CreateFailureDefinition(id, FailureSeverity.Warning, message);
            created++;
        }

        services.Log.Info(
            "cabling failure definitions: {0} of {1} registered, the rest were already in this session",
            created,
            Declared.Length);
    }

    /// <summary>
    /// Nothing to undo, and the API is the reason rather than an oversight.
    /// </summary>
    /// <remarks>
    /// <c>FailureDefinition</c> has no member that removes one - checked against the metadata of all
    /// four releases - and the registry is documented as one per session. This is the single
    /// irreversible registration in the whole family, which is why it was chosen to go first: an
    /// identifier that reaches a customer's session cannot be withdrawn, only left alone.
    /// </remarks>
    public void Stop()
    {
    }
}
