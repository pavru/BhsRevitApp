using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace BHS.MEP.Cabling.Ui;

/// <summary>
/// One category this project counts as a carrier, as the screen holds it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two names, and both are load-bearing.</b> <see cref="Category"/> is what goes into the model -
/// the identifier, the same on every Revit in every language - and <see cref="Name"/> is what this
/// Revit calls it, which is the only one a person can recognise. Storing the display name would make
/// a catalogue written on a Russian Revit unreadable on an English one, which is the failure this
/// repository has already had with parameter names.
/// </para>
/// <para>
/// <b>It counts itself, and that is the whole point of the screen.</b> A rule naming a parameter
/// spelled slightly wrong, or a value since renamed, admits nothing - and a run then comes back
/// short, which reads as a model with no structure in it. Here the number is beside the rule while
/// it is being written, so the mistake is caught before it is saved rather than after a route is
/// missing.
/// </para>
/// </remarks>
public sealed class CatalogueRule : INotifyPropertyChanged
{
    private string _class = string.Empty;
    private string _parameter = string.Empty;
    private string _value = string.Empty;
    private int _seen;
    private int _counted;
    private bool _counting = true;

    public CatalogueRule(string category, string name)
    {
        Category = category;
        Name = name;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The identifier stored in the model, never shown as the label.</summary>
    public string Category { get; }

    /// <summary>What this Revit calls the category.</summary>
    public string Name { get; }

    /// <summary>What class its elements count as - the conduit preference is expressed in this.</summary>
    public string Class
    {
        get => _class;
        set => Set(ref _class, value ?? string.Empty);
    }

    /// <summary>The type parameter that decides whether an element counts, or empty for all of them.</summary>
    public string Parameter
    {
        get => _parameter;
        set => Set(ref _parameter, value ?? string.Empty);
    }

    /// <summary>What that parameter has to hold, or empty for "anything at all".</summary>
    public string Value
    {
        get => _value;
        set => Set(ref _value, value ?? string.Empty);
    }

    /// <summary>Elements of this category in the model and its links.</summary>
    public int Seen
    {
        get => _seen;
        set => Set(ref _seen, value);
    }

    /// <summary>How many of them the rule lets through.</summary>
    public int Counted
    {
        get => _counted;
        set => Set(ref _counted, value);
    }

    /// <summary>Whether the counts have been taken since the rule last changed.</summary>
    public bool Counting
    {
        get => _counting;
        private set => Set(ref _counting, value);
    }

    /// <summary>Whether this rule, as written, admits nothing at all.</summary>
    public bool Empty => !Counting && Counted == 0;

    /// <summary>The counts in words, or what is being waited for.</summary>
    public string Tally =>
        Counting ? "counting…"
        : Parameter.Trim().Length == 0 ? $"{Counted} element(s)"
        : $"{Counted} of {Seen} element(s)";

    /// <summary>Marks the counts as no longer describing what is written.</summary>
    internal void Recounting() => Counting = true;

    /// <summary>Records what a count found.</summary>
    internal void Counts(int seen, int counted)
    {
        Seen = seen;
        Counted = counted;
        Counting = false;
        Raise(nameof(Tally));
        Raise(nameof(Empty));
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;

        field = value;
        Raise(name);

        // The derived ones by hand, for the reason the routing view model gives: a framework that
        // worked them out would be a package, and inside Revit a package is an assembly somebody
        // else may also ship.
        switch (name)
        {
            case nameof(Counting):
            case nameof(Counted):
            case nameof(Seen):
                Raise(nameof(Tally));
                Raise(nameof(Empty));
                break;
        }
    }

    private void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// One of the six installation-method slots, as the screen holds it.
/// </summary>
/// <remarks>
/// <b>The number is the point, not the order of a list.</b> Every circuit writes the method named here
/// into the pair of parameters with this number, so a schedule column is a slot; renaming a slot in a
/// project that already wrote lengths makes that column mean something else from the next apply on.
/// </remarks>
public sealed class MethodSlot : INotifyPropertyChanged
{
    private string _name = string.Empty;

    public MethodSlot(int number, string name)
    {
        Number = number;
        _name = name ?? string.Empty;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Which pair of circuit parameters this slot writes, one-based.</summary>
    public int Number { get; }

    /// <summary>The label, for the column the number sits in.</summary>
    public string Caption => "Slot " + Number;

    /// <summary>The method as the carriers' type parameter spells it, or empty for an unused slot.</summary>
    public string Name
    {
        get => _name;
        set
        {
            var said = value ?? string.Empty;

            if (string.Equals(_name, said, StringComparison.Ordinal))
                return;

            _name = said;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
        }
    }
}

/// <summary>What the window hands over to be written: the carrier rules and the methods beside them.</summary>
public sealed class CatalogueDraft
{
    public CatalogueDraft(IReadOnlyList<CatalogueRule> rules, string methodParameter, IReadOnlyList<string> methods)
    {
        Rules = rules;
        MethodParameter = methodParameter;
        Methods = methods;
    }

    public IReadOnlyList<CatalogueRule> Rules { get; }

    /// <summary>The carriers' type parameter the method is read from, trimmed; empty for off.</summary>
    public string MethodParameter { get; }

    /// <summary>The method named in each slot, slot 1 first, trimmed; empty for unused.</summary>
    public IReadOnlyList<string> Methods { get; }
}

/// <summary>
/// A category a person may add to the catalogue, by the name their Revit gives it.
/// </summary>
public sealed class CatalogueCategory
{
    public CatalogueCategory(string category, string name)
    {
        Category = category;
        Name = name;
    }

    /// <summary>The identifier, as stored.</summary>
    public string Category { get; }

    /// <summary>What this Revit calls it.</summary>
    public string Name { get; }

    public override string ToString() => Name;
}

/// <summary>
/// The screen that decides which of a project's elements carry cable.
/// </summary>
/// <remarks>
/// <para>
/// <b>It exists because the catalogue lives in the model and nothing else can write there</b> - the
/// owner's answer of 2026-09-22 to where the rules are kept, and to whether a person must be able to
/// change them. The model's settings are a flat store inside the document with no editor of any
/// kind; without this window the feature would be configurable only by a program.
/// </para>
/// <para>
/// <b>On the plain axis, like every other screen here.</b> It never names a Revit type: the
/// categories arrive as names already resolved, counting is a function it is handed, and saving is
/// another. That is what keeps a <c>Document</c> off a background thread and lets the screen be
/// driven without Revit at all.
/// </para>
/// <para>
/// <b>Counting is synchronous, and that is measured rather than assumed.</b> This window is shown
/// from the API thread by a modal command, and reads from inside a modal dialog answer on all four
/// releases - the measurement recorded for the routing window. Counting a category is one collector
/// and a type lookup per element, with the answers cached by type.
/// </para>
/// </remarks>
public sealed class CatalogueViewModel : INotifyPropertyChanged
{
    private readonly Func<CatalogueRule, (int Seen, int Counted)> _count;
    private readonly Func<CatalogueDraft, string> _save;
    private readonly IReadOnlyList<CatalogueRule> _shipped;

    private CatalogueCategory? _chosen;
    private string _methodParameter = string.Empty;
    private string _saved = string.Empty;
    private bool _dirty;

    /// <param name="rules">What the project says today, which is where the table starts.</param>
    /// <param name="categories">Every category this Revit offers, by the name it gives them.</param>
    /// <param name="shipped">The defaults, for the button that puts them back.</param>
    /// <param name="count">How many elements a rule sees and how many it admits.</param>
    /// <param name="save">Writes the table into the model; returns what went wrong, or empty.</param>
    /// <param name="methodParameter">The carriers' type parameter the installation method is read from.</param>
    /// <param name="methods">The method in each of the six slots, slot 1 first.</param>
    public CatalogueViewModel(
        IReadOnlyList<CatalogueRule> rules,
        IReadOnlyList<CatalogueCategory> categories,
        IReadOnlyList<CatalogueRule> shipped,
        Func<CatalogueRule, (int Seen, int Counted)> count,
        Func<CatalogueDraft, string> save,
        string methodParameter = "",
        IReadOnlyList<string>? methods = null)
    {
        _count = count;
        _save = save;
        _shipped = shipped;

        Categories = categories;
        Rules = new ObservableCollection<CatalogueRule>();

        _methodParameter = methodParameter ?? string.Empty;
        Methods = new ObservableCollection<MethodSlot>();

        // Six, always, and never a seventh - the owner's fourth answer. A method with no slot is laid
        // into the other length and named on the run screen; the window does not offer a place for it.
        for (var i = 0; i < SlotCount; i++)
        {
            var slot = new MethodSlot(i + 1, methods is not null && i < methods.Count ? methods[i] : string.Empty);

            slot.PropertyChanged += (_, _) =>
            {
                Dirty();
                Raise(nameof(MethodWarning));
                Raise(nameof(HasMethodWarning));
            };

            Methods.Add(slot);
        }

        foreach (var rule in rules)
            Add(rule);

        Rules.CollectionChanged += (_, _) =>
        {
            Dirty();
            Raise(nameof(Warning));
            Raise(nameof(HasWarning));
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>How many installation-method slots a circuit has.</summary>
    public const int SlotCount = 6;

    /// <summary>The carriers' type parameter the installation method is read from; empty turns methods off.</summary>
    public string MethodParameter
    {
        get => _methodParameter;
        set
        {
            var said = value ?? string.Empty;

            if (string.Equals(_methodParameter, said, StringComparison.Ordinal))
                return;

            _methodParameter = said;
            Raise(nameof(MethodParameter));
            Dirty();
            Raise(nameof(MethodWarning));
            Raise(nameof(HasMethodWarning));
        }
    }

    /// <summary>The six slots, slot 1 first.</summary>
    public ObservableCollection<MethodSlot> Methods { get; }

    /// <summary>What is wrong with the methods as written, or empty.</summary>
    /// <remarks>
    /// Said where the mistake is made, like <see cref="Warning"/>: a method named in two slots would split
    /// its length between two schedule columns, and slots named with no parameter to read them from do
    /// nothing at all - both look fine until a schedule is built.
    /// </remarks>
    public string MethodWarning
    {
        get
        {
            var named = Methods.Where(one => one.Name.Trim().Length != 0).ToList();

            var twice = named
                .GroupBy(one => one.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => "'" + group.Key + "' (slots " + string.Join(", ", group.Select(one => one.Number)) + ")")
                .ToList();

            if (twice.Count != 0)
                return "A method is named in more than one slot: " + string.Join("; ", twice)
                    + ". Its length would be split between two columns of every schedule.";

            if (MethodParameter.Trim().Length == 0 && named.Count != 0)
                return "The slots are named but no type parameter is, so lengths by method are off:"
                    + " name the parameter the carrier types say their method in.";

            if (MethodParameter.Trim().Length != 0 && named.Count == 0)
                return "The parameter is named but no slot is, so every metre laid along carriers goes"
                    + " into the other length.";

            return string.Empty;
        }
    }

    public bool HasMethodWarning => MethodWarning.Length > 0;

    /// <summary>What the project counts as a carrier, one row per category.</summary>
    public ObservableCollection<CatalogueRule> Rules { get; }

    /// <summary>Every category that can be added.</summary>
    public IReadOnlyList<CatalogueCategory> Categories { get; }

    /// <summary>The one about to be added.</summary>
    public CatalogueCategory? Chosen
    {
        get => _chosen;
        set => Set(ref _chosen, value);
    }

    /// <summary>Whether the chosen category can be added - it must exist and not be in the table.</summary>
    public bool CanAdd =>
        Chosen is { } chosen
        && !Rules.Any(one => string.Equals(one.Category, chosen.Category, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether there is anything to write.</summary>
    public bool CanSave => _dirty;

    /// <summary>What saving came to, once it has been done.</summary>
    public string Saved
    {
        get => _saved;
        private set => Set(ref _saved, value);
    }

    public bool HasSaved => Saved.Length > 0;

    /// <summary>
    /// The rules that admit nothing, named. Empty when every rule admits something.
    /// </summary>
    /// <remarks>
    /// <b>This is the owner's warning, said where the mistake is made.</b> A Revit failure cannot
    /// carry it: the registered text is all Revit shows - <c>SetMessageString</c> is in the help and
    /// compiles on none of the four releases - so it could not name the category, and there is no
    /// element to point it at. Said here it arrives before the rule is saved rather than after a run
    /// comes back short.
    /// </remarks>
    public string Warning
    {
        get
        {
            var empty = Rules.Where(one => one.Empty).Select(one => one.Name).ToList();

            if (empty.Count == 0)
                return string.Empty;

            return "Nothing counts as a carrier in: " + string.Join(", ", empty)
                + ". Check the parameter name and its value - a rule that matches nothing reads"
                + " exactly like a model that holds nothing.";
        }
    }

    public bool HasWarning => Warning.Length > 0;

    /// <summary>Puts the chosen category into the table.</summary>
    public void AddChosen()
    {
        if (Chosen is not { } chosen || !CanAdd)
            return;

        // Tray rather than nothing: a class is required, and the one a new category most often
        // behaves like is the open one. It is a starting value in a field the person is looking at,
        // not a decision made for them.
        Add(new CatalogueRule(chosen.Category, chosen.Name) { Class = "tray" });
        Chosen = null;
    }

    /// <summary>Takes a category out of the table.</summary>
    public void Remove(CatalogueRule? rule)
    {
        if (rule is not null)
            Rules.Remove(rule);
    }

    /// <summary>Puts back what the product ships, so a project can undo its own catalogue.</summary>
    /// <remarks>
    /// The way out of the one trap in replacing rather than adding: a project that declared its
    /// carriers and left the trays out gets no trays, and this is how it gets them back without
    /// knowing what the defaults were.
    /// </remarks>
    public void RestoreShipped()
    {
        Rules.Clear();

        foreach (var rule in _shipped)
            Add(new CatalogueRule(rule.Category, rule.Name) { Class = rule.Class });
    }

    /// <summary>Counts every row again, against the model as it stands.</summary>
    public void Recount()
    {
        foreach (var rule in Rules)
            Recount(rule);
    }

    /// <summary>Writes the table into the model.</summary>
    public void Save()
    {
        var failed = _save(new CatalogueDraft(
            Rules.ToList(),
            MethodParameter.Trim(),
            Methods.Select(one => one.Name.Trim()).ToList()));

        if (failed.Length != 0)
        {
            Saved = failed;
            return;
        }

        _dirty = false;
        Raise(nameof(CanSave));
        Saved = "Saved into the model. The next run reads these.";
    }

    private void Add(CatalogueRule rule)
    {
        rule.PropertyChanged += OnRuleChanged;
        Rules.Add(rule);
        Recount(rule);
    }

    private void OnRuleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not CatalogueRule rule)
            return;

        // Only the three a person types. Counting on a change of the counts themselves would ask
        // the model again for every answer it just gave.
        if (e.PropertyName is not (nameof(CatalogueRule.Class)
            or nameof(CatalogueRule.Parameter)
            or nameof(CatalogueRule.Value)))
        {
            return;
        }

        Dirty();

        if (e.PropertyName != nameof(CatalogueRule.Class))
            Recount(rule);
    }

    private void Recount(CatalogueRule rule)
    {
        rule.Recounting();

        var counts = _count(rule);

        rule.Counts(counts.Seen, counts.Counted);

        Raise(nameof(Warning));
        Raise(nameof(HasWarning));
    }

    private void Dirty()
    {
        _dirty = true;
        Saved = string.Empty;
        Raise(nameof(CanSave));
        Raise(nameof(CanAdd));
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;

        field = value;
        Raise(name);

        switch (name)
        {
            case nameof(Chosen):
                Raise(nameof(CanAdd));
                break;

            case nameof(Saved):
                Raise(nameof(HasSaved));
                break;
        }
    }

    private void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
