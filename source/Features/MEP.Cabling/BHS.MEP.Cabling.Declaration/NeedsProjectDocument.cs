using BHS.Revit.Abstractions;

namespace BHS.MEP.Cabling.Declaration;

/// <summary>
/// Offers a cabling command only when a project is open: a document, and not a family.
/// </summary>
/// <remarks>
/// <para>
/// <b>Grey only for a reason the person can see.</b> A command that cannot run because there is no
/// document is obvious the moment you look at Revit, and so is being in the family editor - the
/// window says so. A command greyed out for a reason the user cannot see is worse than one that runs
/// and explains itself, so everything else - no trays in the model, no circuits, no parameters bound -
/// is left to the command, which can say so.
/// </para>
/// <para>
/// <b>Not in the family editor, and that is the owner's decision (2026-09-13):</b> cabling commands
/// do not work there. Greying them is the visible half of that decision; the refusal below is the
/// half that holds.
/// </para>
/// <para>
/// <b>Advice, not a gate, so the commands refuse a family document as well.</b> Availability greys a
/// ribbon button; whether Revit asks it before running a command reached through the quick access
/// toolbar or a keyboard shortcut has not been measured. Each cabling command therefore checks the
/// same two things itself and says why it will not run - a rule that is the only thing standing
/// between a family document and a command is a rule the first shortcut walks round.
/// </para>
/// <para>
/// <b>Why a rule lives in the declaration at all.</b> Revit asks an availability class while the tab
/// holding its buttons is shown, and not at all while the tab is hidden - measured, for a class
/// declared in the add-in's own assembly. That a rule is asked the same way, through
/// <c>AvailabilityEntryPoint</c> in <c>BHS.Revit.Abstractions</c> behind a class in the feature's Entry
/// assembly, was measured on all four releases on 2026-09-14 by the probe's Gate rule, on the API
/// thread every time; this rule's own buttons are checked by a person, not by the sweep. Either way
/// the asking comes before anybody presses anything, so the rule cannot live in the implementation
/// without loading it. The declaration is
/// loaded at startup anyway and is forbidden to reference the implementation, and this reads nothing
/// but <see cref="AvailabilityContext"/>: one property of the document Revit handed over, no collector,
/// no settings, nothing awaited. That is what keeps it inside the declaration's rule rather than a way
/// round it.
/// </para>
/// <para>
/// No state, and none is possible: the entry point builds a new rule on every call.
/// </para>
/// </remarks>
public sealed class NeedsProjectDocument : IAvailabilityRule
{
    public bool IsAvailable(AvailabilityContext context) =>
        context.Document is { IsFamilyDocument: false };
}
