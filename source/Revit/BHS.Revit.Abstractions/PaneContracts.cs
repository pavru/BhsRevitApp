using System.Windows;
using Autodesk.Revit.DB;

namespace BHS.Revit.Abstractions;

/// <summary>
/// What a feature writes to put something in a dockable pane: the class a pane's manifest entry
/// names as its content.
/// </summary>
/// <remarks>
/// <para>
/// <b>The host constructs it, not Revit.</b> Revit is handed the host's own creator, and the host
/// resolves this class by name - assembly and class, both strings in the manifest - the first time
/// Revit asks for the pane. So the rule that a button's classes live in the assembly the button names
/// does not reach this class: Revit never sees its name. It lives in the feature's own assembly, the
/// one that stays unloaded until a press, and needs a public parameterless constructor. RefCheck's
/// <c>RVTPAN001</c> and <c>RVTPAN002</c> check both against the built assembly, because a string that
/// names nothing would otherwise be found by a person opening the pane.
/// </para>
/// <para>
/// <b>Everything around the content is the host's.</b> The frame that says "open a model", the
/// waiting state, the theme dictionaries and the current document are written once, in the shell,
/// so that no feature writes them again and differently. The content is created once and kept for
/// the session: it is told the document changed rather than rebuilt, because rebuilding loses the
/// scroll position, the selection and the focus - which is what a pane is kept open for.
/// </para>
/// <para>
/// An implementation may also be <see cref="IDisposable"/>; the host disposes it when Revit shuts down.
/// </para>
/// </remarks>
public interface IPaneContent
{
    /// <summary>Builds what the pane shows. Called once per session, on Revit's UI thread.</summary>
    /// <remarks>
    /// Called from inside Revit's request for the pane, which is not a Revit API context as far as
    /// anybody has measured - so this builds WPF objects and nothing else. Anything the content needs
    /// from the model it asks for through <see cref="IPaneContext.ReadAsync{T}"/>.
    /// </remarks>
    FrameworkElement Create(IPaneContext context);

    /// <summary>The document the pane is about changed. On Revit's UI thread.</summary>
    /// <param name="document">
    /// The document now current, or null when there is none - in which case the shell is already
    /// showing its own "no document" state and the content should drop what it holds.
    /// </param>
    void DocumentChanged(PaneDocument? document);
}

/// <summary>What the host gives a pane's content, once, when it is created.</summary>
/// <remarks>
/// <para>
/// <b>No <c>UIApplication</c> and no <c>Document</c> are kept here, on purpose.</b> A pane is
/// modeless: code in it runs from WPF events, which are not a Revit API context, and a document or a
/// session reachable from a click handler is a document read from the wrong place - the most common
/// mistake of this project, measured more than once. Both are reachable only inside the pump, through
/// <see cref="ReadAsync{T}"/> or <see cref="IUiFeatureServices.Pump"/>.
/// </para>
/// </remarks>
public interface IPaneContext
{
    /// <summary>The feature's services, narrowed to its module's settings section and log category.</summary>
    IUiFeatureServices Services { get; }

    /// <summary>The document the pane is about, as the shell last saw it. Null when there is none.</summary>
    PaneDocument? Document { get; }

    /// <summary>Shows the waiting state until the returned object is disposed.</summary>
    /// <param name="caption">
    /// Required, and specific: "Reading cable routes", not "Loading". A waiting state that does not say
    /// what it waits for reads the same as one that has hung.
    /// </param>
    /// <remarks>Nestable, and callable from any thread; the shell marshals to its own.</remarks>
    IDisposable Busy(string caption);

    /// <summary>Reads from the pane's current document on the API thread, through the pump.</summary>
    /// <param name="name">What the work is, for the log.</param>
    /// <param name="read">Runs inside the pump, handed the document the shell considers current.</param>
    /// <param name="cancellationToken">Cancels the wait, not work the pump has already started.</param>
    /// <returns>
    /// What <paramref name="read"/> returned, or <c>default</c> when by the time the pump ran there was
    /// no current document any more. The shell has shown that state already; the caller need not.
    /// </returns>
    Task<T?> ReadAsync<T>(string name, Func<Document, T> read, CancellationToken cancellationToken = default);
}

/// <summary>What a pane is told about the document it is about - a description, not the document.</summary>
/// <remarks>
/// Deliberately not a <c>Document</c>: see <see cref="IPaneContext"/>. Settable rather than
/// <c>init</c>, for the same reason as <c>FeatureButton</c>: <c>init</c> on <c>net48</c> needs a
/// polyfill in the AppDomain Revit 2024 shares with every other vendor.
/// </remarks>
public sealed class PaneDocument
{
    public PaneDocument(string title, bool isFamilyDocument)
    {
        Title = title ?? string.Empty;
        IsFamilyDocument = isFamilyDocument;
    }

    /// <summary>The document's title, as Revit gives it.</summary>
    public string Title { get; }

    /// <summary>Whether this is a family rather than a project. A pane decides what that means for it.</summary>
    public bool IsFamilyDocument { get; }

    public override string ToString() => IsFamilyDocument ? Title + " (family)" : Title;
}
