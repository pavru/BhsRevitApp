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
/// <b>This assembly is not identifiers alone.</b> <see cref="NeedsProjectDocument"/> sits beside this
/// class: the availability rule the cabling buttons ask, a decision that executes before anybody
/// presses anything, because Revit asks it whenever the tab holding the buttons is shown. It is here
/// for the reason the identifiers are - loaded at startup anyway, and forbidden to reach the
/// implementation - and it keeps to that by reading only the context Revit passes.
/// </para>
/// <para>
/// <b>This type is also how the buttons find their edition.</b> An edition offers cabling by listing
/// this module; the host builds the buttons of <c>BHS.MEP.Cabling.Entry</c> only for an edition that
/// does, and each command entry point there names this type to find the host that declared it.
/// </para>
/// <para>
/// <b>Why the identifiers are here and not in the manifest.</b> The ribbon is declared in a manifest -
/// the one <c>BHS.MEP.Cabling.Entry</c> carries - because Revit reads it as strings and nothing of ours
/// needs to agree with it as a literal. These are the other case: <b>two of our own sides have to
/// name the same thing</b> - whoever registers at
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
/// <b>Severity is <c>Warning</c> for all five, and that is a decision rather than a default.</b> An
/// <c>Error</c> rolls the transaction back, and every one of these describes work that should happen
/// all the same: a circuit the router could not reach is still a circuit the rest of the run served.
/// </para>
/// <para>
/// <b>A warning is read by whoever is in front of Revit when it shows it, and by nobody later.</b>
/// This said once that a warning stays in the model's warning list, where a reviewer looks. Revit's
/// own reference for <c>Document.PostFailure</c> says the opposite - "warnings posted via this method
/// will not be stored in the document after they are resolved" - and the owner's decision of
/// 2026-09-14 accepts it: a failure that is resolved is forgotten. So the second addressee is the
/// person who pressed Apply, at the moment Revit puts the warning in front of them; the result screen
/// and the log are what remains afterwards.
/// </para>
/// <para>
/// <b>The registered string is the whole of what Revit will show, so each one has to stand alone.</b>
/// This said the opposite first - that the text could be set per occurrence, on the strength of
/// <c>FailureMessage.SetMessageString</c> appearing in the reference XML of all four releases. The
/// compiler refused that member on all four alike; its XML article has no summary, only exceptions;
/// and Autodesk's own calls, six of them across two SDKs, post a message without setting any text.
/// What a warning can still say about itself is <i>which</i> element it is about, through
/// <c>SetFailingElement</c> - so these sentences are written to be true of every case they will ever
/// cover, and the particulars live on the result screen and in the log.
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
    /// <summary>A circuit with an end - its panel or one of its devices - that has no carrier within reach.</summary>
    /// <remarks>
    /// <para>
    /// The most valuable of the five, measured: on the first real model this condition found a
    /// consumer of an adjacent discipline that nobody had routed carriers to - in either discipline.
    /// It is a fault in the model rather than a choice about how to wire, and it reads differently
    /// from the rest for that reason.
    /// </para>
    /// <para>
    /// <b>Its text names the circuit, because the circuit is what it is posted against.</b> It said
    /// "this device" first, while the apply set the circuit as the failing element - so the element
    /// Revit selected was never the thing the sentence was about. And the router stops at whichever
    /// end has nothing near, which is as often the panel as a device, so the sentence says both.
    /// </para>
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

    /// <summary>An indicator of ours that somebody connected to other elements and left as an indicator.</summary>
    /// <remarks>
    /// <para>
    /// <b>The owner's decision of 2026-09-14.</b> An element of the indicator type, carrying our
    /// recommendation and connected to something, is left exactly as it is: the apply neither rewrites
    /// it nor removes it. But nothing about it says it stopped being an indicator, so the tool still
    /// reads it as one, and a designer who meant it to be a real box would never find that out from the
    /// model. This is how they do.
    /// </para>
    /// <para>
    /// <b>The sentence puts each consequence on the part that causes it, because the likely remedy
    /// depends on it.</b> The reader goes by the <i>type</i> alone: every element of it is an indicator,
    /// left out of the network and never taken for a real box. The <i>recommendation</i> is what only the
    /// apply goes by, to recognise the element as its own. A first wording blamed both equally, and the
    /// action it invited - clearing the recommendation - silences this warning and changes nothing else:
    /// the element is still read as an indicator, and the next apply, no longer finding one of its own
    /// there, places a second one beside it. The parameter is named because "the recommendation" is not
    /// something anybody can find in the properties palette.
    /// </para>
    /// <para>
    /// <b>Posted for every such indicator the apply meets</b>, whether or not the run recommends a box
    /// at its place. One standing where a box is recommended still takes that box's place, so no
    /// second indicator appears beside it; that does not make it any less a designer's element dressed
    /// as ours.
    /// </para>
    /// <para>
    /// The sentence says "connected to other elements", not "joined to a tray or a conduit" and not
    /// "part of the network": the test is any connector that is connected, to anything - another
    /// indicator included - and the registered string has to be true of every occurrence.
    /// </para>
    /// </remarks>
    public static readonly FailureDefinitionId IndicatorJoinedIntoNetwork =
        new(new Guid("7ee44da0-8299-470b-8d1a-71fd5e77bbd0"));

    private static readonly (FailureDefinitionId Id, string Message)[] Declared =
    {
        (NoCarrierNear,
            "An end of this circuit - its panel or one of its devices - has no cable tray or conduit within reach, "
            + "so no route could be found for this circuit."),
        (NoConnectivity,
            "This circuit's devices cannot be reached through the cable trays and conduits of this model."),
        (ConnectionUnreadable,
            "This circuit's cable connection type could not be read, so the project default was used."),
        (JunctionBoxJoinedToNothing,
            "This element's type calls it a junction box, but it is not joined to any cable tray or conduit."),
        (IndicatorJoinedIntoNetwork,
            "This junction box indicator has been connected to other elements, but it still has the indicator type. "
            + "Cable routing reads every element of that type as an indicator and leaves it out of the network; "
            + "and because it also still carries the tool's recommendation (BHS_Cbl_Recommendation), cable routing "
            + "leaves it as it is."),
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
