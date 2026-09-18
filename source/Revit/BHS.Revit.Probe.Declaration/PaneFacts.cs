using System.Threading;

namespace BHS.Revit.Probe.Declaration;

/// <summary>
/// What the probe pane's content writes down, where the probe can read it without loading the pane's
/// assembly.
/// </summary>
/// <remarks>
/// <para>
/// <b>Here, and not in the pane's own assembly</b>, for the same reason <see cref="GateRule"/> keeps its
/// counters here: the whole point of <c>BHS.Revit.Probe.Pane</c> is that it stays out of the AppDomain
/// until Revit asks for the pane, and a probe that read a static off it would load it to ask. The
/// declaration is loaded at startup anyway - the probe lists its module.
/// </para>
/// <para>
/// Plain values and one delegate, and nothing of WPF: a declaration may name nothing beyond its short
/// allowed list, and the theme questions are asked of the live element by the content itself, through
/// <see cref="Inspect"/>, on the UI thread the probe posts to.
/// </para>
/// </remarks>
public static class PaneFacts
{
    private static int _created;
    private static int _createThread;
    private static int _documentChanges;
    private static string _lastDocument = string.Empty;
    private static string _readTitle = string.Empty;

    /// <summary>How many times the host called the content's <c>Create</c>.</summary>
    public static int Created => Volatile.Read(ref _created);

    /// <summary>The managed thread of the first <c>Create</c>; zero before it.</summary>
    public static int CreateThread => Volatile.Read(ref _createThread);

    /// <summary>How many times the content was told the document changed.</summary>
    public static int DocumentChanges => Volatile.Read(ref _documentChanges);

    /// <summary>The title in the last change, empty for none.</summary>
    public static string LastDocument => Volatile.Read(ref _lastDocument);

    /// <summary>The title the content read through the pump, the first time it had a document.</summary>
    public static string ReadTitle => Volatile.Read(ref _readTitle);

    /// <summary>
    /// Set by the content when it is created: answers the theme questions about the live element. Null
    /// until the pane has been created. Called on the element's own thread.
    /// </summary>
    public static Func<IDictionary<string, string>>? Inspect { get; set; }

    public static void RecordCreate()
    {
        Interlocked.CompareExchange(ref _createThread, Environment.CurrentManagedThreadId, 0);
        Interlocked.Increment(ref _created);
    }

    public static void RecordDocument(string? title)
    {
        Volatile.Write(ref _lastDocument, title ?? string.Empty);
        Interlocked.Increment(ref _documentChanges);
    }

    public static void RecordRead(string? title) => Volatile.Write(ref _readTitle, title ?? string.Empty);
}
