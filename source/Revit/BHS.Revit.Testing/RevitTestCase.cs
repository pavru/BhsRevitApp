using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace BHS.Revit.Testing;

/// <summary>
/// One thing a test asserts, and what it needs to be able to assert it.
/// </summary>
/// <remarks>
/// <para>
/// <b>A case is one outcome, not a bag of assertions.</b> It maps onto one line of the sweep report,
/// which is the form every other check in this repository already takes; a case that reported five
/// results would be five checks wearing one name, and the floor that guards against a check quietly
/// disappearing counts lines.
/// </para>
/// <para>
/// <b>What it needs is declared, not discovered.</b> A case that reads a model says so, and the
/// harness reports it as skipped - by name, with the reason - when the sweep is running without one.
/// The alternative, letting it dereference a null document and fail, would report a defect in the
/// code under test where there is only a mode of the run; and the alternative to that, letting it
/// quietly return, is the failure this whole repository keeps meeting: a check that is never
/// evaluated prints nothing and reads exactly like one that passed.
/// </para>
/// </remarks>
public sealed class RevitTestCase
{
    /// <param name="name">
    /// What is being asserted, as a sentence. It becomes the text of a check in the sweep report,
    /// read months later by somebody who did not write it, so "markers are excluded from the
    /// carrier network" earns its length over "MarkerTest2".
    /// </param>
    /// <param name="run">The body. It throws to fail; see <see cref="Expect"/>.</param>
    /// <param name="needsDocument">
    /// Whether the case reads a model. Declared rather than inferred, because the harness has to
    /// decide before the body runs.
    /// </param>
    /// <param name="writes">
    /// Whether the case modifies the document. The harness then wraps it in a
    /// <see cref="TransactionGroup"/> and rolls that group back, always - see
    /// <see cref="RevitTestRun"/> for why the discipline belongs to the harness rather than to the
    /// author of each case.
    /// </param>
    public RevitTestCase(
        string name,
        Action<RevitTestContext> run,
        bool needsDocument = false,
        bool writes = false)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Body = run ?? throw new ArgumentNullException(nameof(run));
        NeedsDocument = needsDocument;
        Writes = writes;
    }

    /// <summary>What this case asserts.</summary>
    public string Name { get; }

    /// <summary>Whether it reads a model, and so cannot run without one.</summary>
    public bool NeedsDocument { get; }

    /// <summary>Whether it modifies the document, and so has to be rolled back.</summary>
    /// <remarks>
    /// A case that writes also needs a document, whatever it declared: there is nothing to write to
    /// otherwise. <see cref="RevitTestRun"/> treats the two as one requirement rather than making
    /// every author remember to say both.
    /// </remarks>
    public bool Writes { get; }

    internal Action<RevitTestContext> Body { get; }
}

/// <summary>
/// A named group of cases, declared by the assembly whose code they are about.
/// </summary>
/// <remarks>
/// <b>Declared and listed by hand, never scanned for.</b> The same rule as feature modules and for
/// the same measured reason: on Revit 2024 every add-in shares one AppDomain, and an assembly loaded
/// to be looked at holds its simple name there for the rest of the session. Scanning a directory to
/// find tests would load every assembly in it, including the ones no run was going to touch.
/// </remarks>
public interface IRevitTestSuite
{
    /// <summary>
    /// Short, and stable: it prefixes every case name in the report, so renaming it renames every
    /// check and makes them all look new to the record CI compares against.
    /// </summary>
    string Name { get; }

    /// <summary>The cases, in the order they should be reported.</summary>
    IEnumerable<RevitTestCase> Cases { get; }
}

/// <summary>What a case is given, and the only way it can say anything besides pass or fail.</summary>
public sealed class RevitTestContext
{
    internal RevitTestContext(UIApplication application, Document? document, List<KeyValuePair<string, string>> notes)
    {
        Application = application;
        Document = document;
        Recorded = notes;
    }

    /// <summary>
    /// The live session. Valid for the duration of the case and no longer - the same rule as
    /// <c>IRevitSession</c>, from which it came, and for the same reason.
    /// </summary>
    public UIApplication Application { get; }

    /// <summary>
    /// The active document, or nothing when the run has no model open.
    /// </summary>
    /// <remarks>
    /// Null only for a case that did not declare <see cref="RevitTestCase.NeedsDocument"/>: one that
    /// did was either given a document or never started. So a case which declared its need may use
    /// this without checking, and one which did not has to.
    /// </remarks>
    public Document? Document { get; }

    internal List<KeyValuePair<string, string>> Recorded { get; }

    /// <summary>
    /// Something worth knowing that cannot fail - a count, a length, a name Revit gave something.
    /// </summary>
    /// <remarks>
    /// The distinction the sweep report already draws between a check and a note, offered to cases
    /// so they need not turn a measurement into a threshold to be allowed to report it. A test that
    /// asserts "between 300 and 400 carriers" against somebody's live building is a test that fails
    /// the day the building changes; a test that asserts an invariant and notes the count says more
    /// and breaks less.
    /// </remarks>
    public void Note(string what, string value) =>
        Recorded.Add(new KeyValuePair<string, string>(what ?? string.Empty, value ?? string.Empty));
}
