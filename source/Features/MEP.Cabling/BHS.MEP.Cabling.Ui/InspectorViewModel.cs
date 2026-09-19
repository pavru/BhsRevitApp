using System.ComponentModel;
using System.Windows.Input;

namespace BHS.MEP.Cabling.Ui;

/// <summary>What the inspector pane shows: one element's stored values, or one sentence.</summary>
/// <remarks>
/// <para>
/// <b>On the plain axis, like the routing window's model, and for the same reason:</b> there is no
/// <c>Document</c> here to reach for. Everything arrives already read and already formatted - lengths by
/// Revit, against the document's own units, inside the pump - as an <see cref="InspectorContent"/>.
/// </para>
/// <para>
/// <b>Two states, never both.</b> An element and its sections, or a sentence - nothing selected, several
/// selected, not ours, gone. None of them is an error, so none of them looks like one.
/// </para>
/// </remarks>
public sealed class InspectorViewModel : INotifyPropertyChanged
{
    private string _message = string.Empty;
    private InspectorContent? _content;

    public InspectorViewModel(Action readAgain)
    {
        ReadAgain = new Command(readAgain ?? throw new ArgumentNullException(nameof(readAgain)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The sentence, when there is no element to show.</summary>
    public string Message => _message;

    public bool HasMessage => _content is null && _message.Length > 0;

    public bool HasElement => _content is not null;

    public string Title => _content?.Title ?? string.Empty;

    public string Kind => _content?.Kind ?? string.Empty;

    public IReadOnlyList<InspectorSection> Sections => _content?.Sections ?? Array.Empty<InspectorSection>();

    public ICommand ReadAgain { get; }

    public string ReadAgainText => InspectorStrings.Get(InspectorStrings.ReadAgain);

    /// <summary>Shows an element. On the UI thread.</summary>
    public void Show(InspectorContent content)
    {
        _content = content ?? throw new ArgumentNullException(nameof(content));
        _message = string.Empty;
        Changed();
    }

    /// <summary>Shows a sentence instead of an element. On the UI thread.</summary>
    public void Say(string message)
    {
        _content = null;
        _message = message ?? string.Empty;
        Changed();
    }

    private void Changed()
    {
        foreach (var name in new[] { nameof(Message), nameof(HasMessage), nameof(HasElement), nameof(Title), nameof(Kind), nameof(Sections), nameof(ReadAgainText) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private sealed class Command : ICommand
    {
        private readonly Action _run;

        public Command(Action run) => _run = run;

        // Never changes, so never raised; the add and remove are there because the interface asks.
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => _run();
    }
}

/// <summary>One element, read and formatted - built inside the pump, shown on the UI thread.</summary>
public sealed class InspectorContent
{
    public InspectorContent(string title, string kind, IReadOnlyList<InspectorSection> sections)
    {
        Title = title ?? string.Empty;
        Kind = kind ?? string.Empty;
        Sections = sections ?? Array.Empty<InspectorSection>();
    }

    /// <summary>The element's name and id, the id in the form Revit's Select by ID accepts.</summary>
    public string Title { get; }

    /// <summary>What the element is, in a few words.</summary>
    public string Kind { get; }

    public IReadOnlyList<InspectorSection> Sections { get; }
}

/// <summary>A group of rows under one heading - what the calculation wrote, or what the designer set.</summary>
public sealed class InspectorSection
{
    public InspectorSection(string heading, string note, IReadOnlyList<InspectorRow> rows)
    {
        Heading = heading ?? string.Empty;
        Note = note ?? string.Empty;
        Rows = rows ?? Array.Empty<InspectorRow>();
    }

    public string Heading { get; }

    /// <summary>A sentence under the heading; empty for none.</summary>
    public string Note { get; }

    public bool HasNote => Note.Length > 0;

    public IReadOnlyList<InspectorRow> Rows { get; }
}

/// <summary>One stored value: what it is, and what stands - or that nothing does.</summary>
public sealed class InspectorRow
{
    public InspectorRow(string label, string value, bool written)
    {
        Label = label ?? string.Empty;
        Value = value ?? string.Empty;
        Written = written;
    }

    public string Label { get; }

    /// <summary>The value, or the words for its absence - "never written", "not in this model".</summary>
    public string Value { get; }

    /// <summary>
    /// Whether a value stands. False reads differently on screen - quieter, in italics - so that "never
    /// written" can never be taken for a value, and a written zero never for an absence.
    /// </summary>
    public bool Written { get; }
}
