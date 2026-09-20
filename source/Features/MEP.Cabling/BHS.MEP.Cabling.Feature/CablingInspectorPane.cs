using System.Globalization;
using System.Windows;
using Autodesk.Revit.DB;
using BHS.Logging;
using BHS.MEP.Cabling.Revit;
using BHS.MEP.Cabling.Ui;
using BHS.Revit.Abstractions;

namespace BHS.MEP.Cabling.Feature;

/// <summary>
/// The cabling inspector: a dockable pane that shows what the cable calculation wrote on the selected
/// element - and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only what was written, read from the model; nothing computed, nothing written</b> - the owner's
/// decision of 2026-09-20. What is read and how it is classified is <see cref="CablingInspection"/>, apart
/// from WPF so that a sweep can assert it; this class decides only when to read and how it reads on screen.
/// </para>
/// <para>
/// <b>Here, in the feature's assembly, and not in an assembly of its own.</b> The content needs exactly what
/// the commands need - the reader in <c>BHS.MEP.Cabling.Revit</c>, the screen in <c>BHS.MEP.Cabling.Ui</c>,
/// Revit's own formatting of lengths in <see cref="CablingLength"/> - and this assembly is the one that stays
/// out of the AppDomain until the feature is wanted, whether by a press or by the pane being shown. A fifth
/// cabling assembly on four TFMs would buy one thing: that a pane Revit restores as shown at startup does
/// not bring the commands in with it. Nothing measures the cabling feature's laziness at startup - the
/// sweep's ribbon check is about the probe's feature - and a pane restored as shown is the feature in use.
/// RVTPAN003 forbids only the Entry and declaration assemblies, which load before any pane is asked for.
/// </para>
/// <para>
/// <b>One element, or a sentence.</b> Nothing selected, several selected, an element that is not ours, a link,
/// an element deleted since it was selected, a family document: each is a plain sentence, not an error.
/// Several selected is the simplest honest answer the owner allowed - a count and "select one" - rather than
/// the first of them, which would be a guess about which one was meant, or a merge, which would be computing.
/// </para>
/// <para>
/// <b>Three things start a read, and none of them is a press.</b> A selection change, a change of document,
/// and the pane becoming visible with a selection change missed behind it. A "read again" button stood at the
/// bottom of the screen until 2026-09-20 and the owner removed it: measured by hand on a live Revit 2026,
/// pressing it left Revit's selection empty and the pane back at "select an element" - it destroyed the one
/// thing it existed to re-read. Why a press costs the selection is the pane mechanism's question, answered in
/// <c>PaneEntryPoint</c>; what this pane keeps from it is that it asks for nothing.
/// </para>
/// <para>
/// <b>Reads the latest selection only, and only while the pane is visible.</b> A click fires a selection
/// change, a drag-select several; each read is a trip through the pump, and a read that comes back after a
/// newer selection is dropped by its generation rather than shown. A selection that changes while the pane is
/// hidden is remembered as stale and read when it is shown again - whether Revit's frame hiding makes the
/// element invisible to WPF is not measured, and if it does not, the cost is reads nobody sees.
/// </para>
/// <para>
/// <b>No waiting state of its own, and no clearing of its own either.</b>
/// <see cref="IPaneContext.ReadAsync{T}"/> already shows the shell's "waiting for Revit" until the pump takes
/// the work, and the read itself runs on the thread that would have to paint a caption - so a caption of ours
/// could never be seen. The shell's waiting layer also hides whatever was shown before it, so the answer about
/// the element selected a moment ago cannot be read as the answer about this one; clearing here as well would
/// be a second mechanism doing the first one's job, and the first one is the one every future pane gets.
/// </para>
/// </remarks>
public sealed class CablingInspectorPane : IPaneContent
{
    private IPaneContext? _context;
    private InspectorViewModel? _model;
    private InspectorView? _view;
    private ILog? _log;
    private int _generation;
    private bool _stale;

    public FrameworkElement Create(IPaneContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _log = context.Services.Log;

        InspectorStrings.Culture = context.Culture;

        _model = new InspectorViewModel();
        _view = new InspectorView(_model);

        _view.IsVisibleChanged += (_, _) =>
        {
            if (_view.IsVisible && _stale)
                Refresh();
        };

        context.SelectionChanged += (_, _) => Refresh();

        Refresh();
        return _view;
    }

    /// <summary>The document changed: whatever is being read is about the old one.</summary>
    /// <remarks>
    /// The read itself is started by the selection change the host raises straight after every change of
    /// document - equal ids in another document being other elements - so reading here too would read twice.
    /// </remarks>
    public void DocumentChanged(PaneDocument? document)
    {
        _generation++;

        if (document is null)
            _model?.Say(string.Empty);
    }

    private void Refresh()
    {
        if (_context is not { } context || _model is not { } model || _view is not { } view)
            return;

        if (!view.IsVisible)
        {
            _stale = true;
            return;
        }

        _stale = false;

        var generation = ++_generation;
        var document = context.Document;

        // No document: the shell is showing its own "open a model" over this content already.
        if (document is null)
        {
            model.Say(string.Empty);
            return;
        }

        if (document.IsFamilyDocument)
        {
            model.Say(InspectorStrings.Get(InspectorStrings.Family));
            return;
        }

        var selection = context.Selection;

        if (selection.Count == 0)
        {
            model.Say(InspectorStrings.Get(InspectorStrings.NothingSelected));
            return;
        }

        if (selection.Count > 1)
        {
            model.Say(InspectorStrings.Format(InspectorStrings.SeveralSelected, selection.Count));
            return;
        }

        Read(context, generation, selection.ElementIds[0]);
    }

    private async void Read(IPaneContext context, int generation, long id)
    {
        Reading? reading;

        try
        {
            reading = await context.ReadAsync("cabling inspector: read the selected element", document => Present(document, id));
        }
        catch (Exception error)
        {
            _log?.Error(error, "cabling inspector: element {0} could not be read", id);
            reading = Reading.Of(InspectorStrings.Format(InspectorStrings.Failed, error.Message));
        }

        OnViewThread(() =>
        {
            // A newer selection, or another document, has been asked for since: this answer is about neither.
            if (generation != _generation || reading is null || _model is not { } model)
                return;

            if (reading.Content is { } content)
                model.Show(content);
            else
                model.Say(reading.Message);
        });
    }

    private void OnViewThread(Action action)
    {
        if (_view is not { } view || view.Dispatcher.CheckAccess())
            action();
        else
            view.Dispatcher.BeginInvoke(action);
    }

    /// <summary>Reads the element and turns it into what the screen shows. Inside the pump, on the API thread.</summary>
    /// <remarks>
    /// Formatting happens here and not on the screen, because <c>UnitFormatUtils</c> is a Revit call and this
    /// is where a Revit call is legal. The screen receives strings.
    /// </remarks>
    private static Reading Present(Document document, long id)
    {
        var inspection = CablingInspection.Read(document, id);

        if (inspection is null)
            return Reading.Of(InspectorStrings.Get(InspectorStrings.Gone));

        switch (inspection.Kind)
        {
            case InspectedKind.NotOurs:
                return Reading.Of(InspectorStrings.Get(InspectorStrings.NotOurs));
            case InspectedKind.Link:
                return Reading.Of(InspectorStrings.Get(InspectorStrings.Link));
        }

        var title = inspection.Name.Length > 0
            ? inspection.Name + " [" + Number(inspection.Id) + "]"
            : "[" + Number(inspection.Id) + "]";

        var sections = new List<InspectorSection>();
        string kind;

        switch (inspection.Kind)
        {
            case InspectedKind.Circuit:
            {
                kind = InspectorStrings.Get(InspectorStrings.KindCircuit);
                var length = CablingLength.Formatter(document);

                sections.Add(Section(
                    InspectorStrings.SectionResult,
                    inspection.CableLength.State == StoredState.NotBound ? InspectorStrings.Get(InspectorStrings.SectionNotBoundNote) : string.Empty,
                    Length(InspectorStrings.CableLength, inspection.CableLength, length),
                    Length(InspectorStrings.InTray, inspection.LengthInTray, length),
                    Length(InspectorStrings.InConduit, inspection.LengthInConduit, length),
                    Length(InspectorStrings.Free, inspection.LengthFree, length),
                    Length(InspectorStrings.Other, inspection.LengthOther, length),
                    Length(InspectorStrings.Slack, inspection.LengthSlack, length),
                    Text(InspectorStrings.RouteConnection, inspection.RouteConnection, Connection, InspectorStrings.NeverWritten),
                    Entries(InspectorStrings.RouteStamp, inspection.RouteStamp)));

                // The designer's input, apart from the calculation's output and labelled so: the two differ
                // exactly where the circuit inherits from its panel, and that difference is worth about 38 %
                // of the length above it.
                sections.Add(Section(
                    InspectorStrings.SectionInput,
                    InspectorStrings.Get(InspectorStrings.SectionInputNote),
                    Text(InspectorStrings.CircuitConnection, inspection.CircuitConnection, Connection, InspectorStrings.NotSet)));
                break;
            }

            case InspectedKind.Indicator:
                kind = InspectorStrings.Get(InspectorStrings.KindIndicator);
                sections.Add(Section(
                    InspectorStrings.SectionResult,
                    string.Empty,
                    Text(InspectorStrings.Recommendation, inspection.Recommendation, Recommendation, InspectorStrings.NeverWritten),
                    Integer(InspectorStrings.TapCount, inspection.TapCount),
                    Entries(InspectorStrings.CircuitRefs, inspection.CircuitRefs)));
                break;

            case InspectedKind.JunctionBox:
                kind = InspectorStrings.Get(InspectorStrings.KindJunctionBox);
                sections.Add(Section(InspectorStrings.SectionResult, string.Empty, Entries(InspectorStrings.CircuitRefs, inspection.CircuitRefs)));
                break;

            default:
                // A carrier: Revit's own name for its category, in Revit's language - a tray is what Revit calls it.
                kind = inspection.Category;
                sections.Add(Section(InspectorStrings.SectionResult, string.Empty, Entries(InspectorStrings.CircuitRefs, inspection.CircuitRefs)));
                break;
        }

        return new Reading(new InspectorContent(title, kind, sections), string.Empty);
    }

    private static InspectorSection Section(string heading, string note, params InspectorRow[] rows) =>
        new(InspectorStrings.Get(heading), note, rows);

    private static InspectorRow Length(string label, Stored<double> value, Func<double, string> format) =>
        value.Written
            ? new InspectorRow(InspectorStrings.Get(label), format(value.Value), written: true)
            : Absent(label, value.State, InspectorStrings.NeverWritten);

    private static InspectorRow Integer(string label, Stored<int> value) =>
        value.Written
            ? new InspectorRow(InspectorStrings.Get(label), value.Value.ToString(InspectorStrings.Culture ?? CultureInfo.InvariantCulture), written: true)
            : Absent(label, value.State, InspectorStrings.NeverWritten);

    private static InspectorRow Text(string label, Stored<string> value, Func<string, string> words, string absent) =>
        value.Written
            ? new InspectorRow(InspectorStrings.Get(label), words(value.Value), written: true)
            : Absent(label, value.State, absent);

    /// <summary>An id list, with its count beside the label - a stamp of thirty carriers is a long line.</summary>
    private static InspectorRow Entries(string label, Stored<string> value)
    {
        if (!value.Written)
            return Absent(label, value.State, InspectorStrings.NeverWritten);

        var entries = CablingInspection.Entries(value.Value);

        return new InspectorRow(
            InspectorStrings.Get(label) + " (" + entries.Count.ToString(InspectorStrings.Culture ?? CultureInfo.InvariantCulture) + ")",
            string.Join("; ", entries),
            written: true);
    }

    private static InspectorRow Absent(string label, StoredState state, string absent) =>
        new(
            InspectorStrings.Get(label),
            InspectorStrings.Get(state == StoredState.NotBound ? InspectorStrings.NotBound : absent),
            written: false);

    /// <summary>A stored connection in words, with the stored value itself beside them - a value is what code compares.</summary>
    private static string Connection(string stored) => stored.Trim() switch
    {
        CablingParameters.ConnectionAtTerminal => InspectorStrings.Get(InspectorStrings.Terminal),
        CablingParameters.ConnectionAtJunctionBox => InspectorStrings.Get(InspectorStrings.JunctionBox),
        var other => other,
    };

    private static string Recommendation(string stored) =>
        string.Equals(stored.Trim(), CablingParameters.JunctionBoxRole, StringComparison.Ordinal)
            ? InspectorStrings.Get(InspectorStrings.RecommendsBox)
            : stored;

    private static string Number(long id) => id.ToString(CultureInfo.InvariantCulture);

    /// <summary>What one read gives the screen: an element, or a sentence.</summary>
    private sealed class Reading
    {
        public Reading(InspectorContent? content, string message)
        {
            Content = content;
            Message = message;
        }

        public InspectorContent? Content { get; }

        public string Message { get; }

        public static Reading Of(string message) => new(null, message);
    }
}
