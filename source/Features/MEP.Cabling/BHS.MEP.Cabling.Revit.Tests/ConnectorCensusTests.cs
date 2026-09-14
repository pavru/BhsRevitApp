using System.Diagnostics;
using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using BHS.MEP.Cabling.Routing;
using BHS.Revit.Testing;

namespace BHS.MEP.Cabling.Revit.Tests;

/// <summary>
/// What the connectors of a model answer when they are asked, counted rather than assumed.
/// </summary>
/// <remarks>
/// <para>
/// <b>A measurement that has two consumers waiting for it.</b> What "joined" means for a logical
/// connector is an open question for the owner, and the code asks <c>IsConnected</c> of every connector,
/// logical ones included, in two places; and a circuit's panel end is taken from the connector it is fed
/// from, which is often logical and has no place. Both rest on what Revit answers, and all that is
/// measured of it is one message, once: "Origin is available only for connectors of PhysicalConn type".
/// The rest is the reference's word. This walks every connector the host and its loaded links hold and
/// counts what each one says.
/// </para>
/// <para>
/// <b>Why catching is right here, when <c>Connectors.OriginOrNull</c> refuses to.</b> Production tests the
/// connector's type rather than catching the exception, because a catch would also swallow the day Revit
/// throws for a different reason, and the hole that leaves in the network would be blamed on somebody's
/// model. The census asks the opposite question - which connectors refuse, and with what - so the refusal
/// is the thing being measured and not an error path. And it keeps production's reason: only Revit's
/// <c>InvalidOperationException</c> itself, the type measured on 2026, counts as a refusal. Its subclasses
/// are not that refusal - nineteen of them in the reference assemblies of all four releases, a stale
/// element, a disabled discipline and a failed regeneration among them - and are matched out by exact
/// type. Anything else is not swallowed but counted by property and type name, and fails the case after
/// the walk, so the census that found it is still written down.
/// </para>
/// <para>
/// <b>Invariants, not answers.</b> What a logical connector says to <c>IsConnected</c> is exactly what
/// nobody knows yet, and a check written before its answer is a check that agrees with whoever wrote it.
/// So the first case asserts only what holds of any model it does not stand down on: every connector says
/// its type and domain, nothing throws but the measured type, every collector and connector manager the
/// walk asks for reads, and every connector set holds as many entries as walking it met - a count Revit
/// gives on its own, where the tables here could only agree with the loop that fills them. The second
/// asserts that this suite's own copy of the reader's ladder is the reader's, which is what makes its
/// notes about the ladder true, and that it walked as many circuits and devices as Revit counts. The
/// answers are notes, and a case that asserts them comes after they have been read.
/// </para>
/// <para>
/// <b>What could hide the walk is asserted before the stand-down.</b> A walk that met no connector because
/// Revit threw, refused a collector or refused every manager would otherwise stand down as a model with
/// nothing to count - a reason that is untrue, printed where a failure belonged.
/// </para>
/// <para>
/// <b>What is walked, and what is not.</b> Family instances, MEP curves - wires among them - fabrication
/// parts and MEP systems, and the case's name says so. Two other owners of a connector manager are not
/// walked, because neither has anything to do with a cable: <c>Structure.Hub</c>, whose
/// <c>GetHubConnectorManager</c> is in the reference assemblies of all four releases, and
/// <c>Structure.AnalyticalElement</c>, whose <c>ConnectorManager</c> is in 2026 and 2027 only. Links are
/// walked one level deep; a link inside a link is counted and not followed. A system's manager lists
/// connectors other elements own, so a connector can be met twice; that is counted as its own note rather
/// than removed, because which connectors a system lists is itself part of the answer.
/// </para>
/// <para>
/// <b>Public by construction.</b> Every note lands in the evidence record of a public repository and the
/// models are somebody's building. Keys carry raw enum values, built-in category names, API class names
/// and counts - never an element, type, family, level or view name, and never an exception's message.
/// Each table is folded past a handful of rows, most frequent first.
/// </para>
/// </remarks>
public sealed class ConnectorCensusTests : IRevitTestSuite
{
    public string Name => "Connector census";

    public IEnumerable<RevitTestCase> Cases => new[]
    {
        new RevitTestCase(
            "every connector of the family instances, MEP curves, fabrication parts and MEP systems of the host and of each link it loads directly reads its type and domain, and every other property asked of it reads or refuses with Revit's InvalidOperationException itself",
            EveryConnectorIsCounted,
            needsDocument: true),

        new RevitTestCase(
            "every described circuit end stands where the first of the reader's ways that answers puts it",
            EveryEndIsWhereTheReaderPutIt,
            needsDocument: true),
    };

    /// <summary>How many rows of a table are noted one by one before the rest are folded into a count.</summary>
    private const int Rows = 10;

    /// <summary>
    /// Walks every connector and counts what it answers; asserts only that the walk is whole and every
    /// connector says its type and domain.
    /// </summary>
    /// <remarks>
    /// The notes are written in a <c>finally</c>, so a case that fails or stands down still leaves what it
    /// counted: the census is most useful exactly when something in it went wrong.
    /// </remarks>
    private static void EveryConnectorIsCounted(RevitTestContext context)
    {
        var clock = Stopwatch.StartNew();
        var census = new ConnectorWalk();

        try
        {
            var host = context.Document!;

            census.Walk(host, linked: false);

            // Document overrides Equals, so two instances of one link type are one document and are
            // walked once. Counted, because a second walk would double every number below.
            var walked = new HashSet<Document> { host };

            foreach (var link in new FilteredElementCollector(host).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                var inLink = link.GetLinkDocument();

                if (inLink is null)
                {
                    census.LinksNotLoaded++;
                    continue;
                }

                if (!walked.Add(inLink))
                {
                    census.LinksAlreadyWalked++;
                    continue;
                }

                census.NestedLinks += new FilteredElementCollector(inLink).OfClass(typeof(RevitLinkInstance)).GetElementCount();
                census.Walk(inLink, linked: true);
            }

            // Before the stand-down: see the class remarks.
            Expect.That(
                census.Unexpected.Count == 0,
                "Revit threw something other than its InvalidOperationException itself while the census asked: " + Listed(census.Unexpected));

            Expect.That(
                census.CollectorsRefused.Count == 0,
                "collectors Revit refused, so no element of their class was walked: " + string.Join(", ", census.CollectorsRefused));

            Expect.That(
                census.ManagersUnread.Count == 0,
                "connector managers or their connector sets that could not be read, so their connectors were not walked, by the class asked for: "
                + Listed(census.ManagersUnread));

            Expect.Same(
                census.SetSizes,
                census.SetEntriesMet,
                "entries the connector sets say they hold, against the entries walking them met (sets whose size did not read, left out of both: "
                + census.SetsUnsized.ToString(CultureInfo.InvariantCulture) + ")");

            Skip.When(
                census.Connectors.Total == 0,
                "the model this sweep opened and its loaded links hold no element with an MEP connector, so there is nothing to count");

            Expect.That(
                census.TypeOrDomainUnread.Count == 0,
                "connectors whose type or domain could not be read: " + Listed(census.TypeOrDomainUnread));
        }
        finally
        {
            clock.Stop();
            census.Note(context, clock.Elapsed);
        }
    }

    /// <summary>
    /// Every circuit's feed and every device's connectors, and which of the reader's ways put each end
    /// where it stands.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The assertion pins the copy, not the model.</b> <see cref="ReaderLadder"/> repeats the reader's
    /// four ways; if the first that answers here does not give the point the reader gave, the notes about
    /// which way answered would be about a ladder the reader does not climb. That is true of any model, and
    /// it is what keeps the notes honest.
    /// </para>
    /// <para>
    /// <b>The host only</b>, as the reader reads circuits: a circuit lives in the file that owns its panel.
    /// Circuits the reader leaves out are still counted in the feed table, with "not described" for the
    /// way, because a panel that offers nothing is one of the answers being looked for.
    /// </para>
    /// <para>
    /// <b>Whole by Revit's own counts, and before the stand-downs:</b> as many electrical systems walked as
    /// the collector counts, and as many entries met in each circuit's element set as its size says.
    /// </para>
    /// </remarks>
    private static void EveryEndIsWhereTheReaderPutIt(RevitTestContext context)
    {
        var clock = Stopwatch.StartNew();
        var ends = new EndWalk();
        CircuitHarvest? harvest = null;

        try
        {
            var document = context.Document!;

            harvest = new CircuitReader().Read(document);
            ends.Walk(document, harvest);

            // Before the stand-downs, as in the first case: a walk that met nothing because Revit threw
            // would otherwise stand down as a model with no circuit.
            Expect.That(
                ends.Unexpected.Count == 0,
                "Revit threw something other than its InvalidOperationException itself while the census asked: " + Listed(ends.Unexpected));

            Expect.Same(
                new FilteredElementCollector(document).OfClass(typeof(ElectricalSystem)).GetElementCount(),
                ends.Circuits + ends.SpareOrSpace,
                "electrical systems Revit counts in the host, against the ones walked as circuits or as spare or space");

            Expect.Same(
                ends.SetSizes,
                ends.SetEntriesMet,
                "elements the walked circuits' sets say they hold, against the entries walking them met (sets whose size did not read, left out of both: "
                + ends.SetsUnsized.ToString(CultureInfo.InvariantCulture) + ")");

            Skip.When(
                ends.Circuits == 0,
                "the model this sweep opened holds no electrical circuit, so there is no end to account for");

            Skip.When(
                harvest.Described.Count == 0,
                "no circuit of the model this sweep opened is described, so the reader put no end anywhere");

            Expect.Same(
                harvest.Described.Count + harvest.Described.Sum(one => one.Devices.Count),
                ends.Accounted,
                "circuit ends the reader described, against the ends this case found again in the model");

            Expect.That(
                ends.Misplaced.Count == 0,
                "circuit ends the first answering way of this suite's copy of the reader does not put where the reader did: "
                + string.Join("; ", ends.Misplaced.Take(Rows)));
        }
        finally
        {
            clock.Stop();
            ends.Note(context, harvest, clock.Elapsed);
        }
    }

    /// <summary>What a property said when it was asked.</summary>
    private enum Said
    {
        Reads,

        /// <summary>Revit's <c>InvalidOperationException</c> itself, the measured refusal; never a subclass of it.</summary>
        Refuses,

        /// <summary>Anything else, which is counted by name and fails the case.</summary>
        ThrowsOther,
    }

    /// <summary>Asks a property, and says whether it answered, refused as measured, or threw anything else.</summary>
    /// <param name="unexpected">Where anything but the measured refusal is counted, by <paramref name="what"/> and type name.</param>
    private static Said Ask<T>(Func<T> read, out T? value, string what, SortedDictionary<string, int> unexpected)
    {
        try
        {
            value = read();
            return Said.Reads;
        }
        catch (Autodesk.Revit.Exceptions.InvalidOperationException refused)
            when (refused.GetType() == typeof(Autodesk.Revit.Exceptions.InvalidOperationException))
        {
            // The exact type: InvalidObjectException, DisabledDisciplineException and the rest of its
            // subclasses would otherwise count as a connector refusing, and pass. They fall to the next
            // clause and are counted by their own name.
            value = default;
            return Said.Refuses;
        }
        catch (Exception other) when (other is not RevitTestSkipped and not RevitTestFailure)
        {
            // Counted, not swallowed: see the class remarks. The type name only - a message is Revit's
            // and may name something in the owner's model.
            value = default;
            Add(unexpected, what + ": " + other.GetType().Name);
            return Said.ThrowsOther;
        }
    }

    private static string Word(Said said) => said switch
    {
        Said.Reads => "reads",
        Said.Refuses => "refuses",
        _ => "throws something else",
    };

    private static string Flag(Said said, bool value) =>
        said == Said.Reads ? (value ? "true" : "false") : Word(said);

    private static string CategoryOf(Element element) =>
        element.Category is { } category ? category.BuiltInCategory.ToString() : "no category";

    private static void Add(SortedDictionary<string, int> tally, string key) =>
        tally[key] = tally.TryGetValue(key, out var count) ? count + 1 : 1;

    private static string Listed(SortedDictionary<string, int> tally) =>
        tally.Count == 0
            ? "none"
            : string.Join("; ", tally.Select(pair => pair.Key + " x" + pair.Value.ToString(CultureInfo.InvariantCulture)));

    private static string Ms(TimeSpan elapsed) =>
        elapsed.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture);

    /// <summary>A count kept apart for the host and the links, as every table here keeps it.</summary>
    private sealed class Split
    {
        public int Host { get; set; }

        public int Links { get; set; }

        public int Total => Host + Links;

        public void Add(bool linked)
        {
            if (linked)
                Links++;
            else
                Host++;
        }

        public override string ToString() =>
            Total.ToString(CultureInfo.InvariantCulture) + " (host " + Host.ToString(CultureInfo.InvariantCulture)
            + ", links " + Links.ToString(CultureInfo.InvariantCulture) + ")";
    }

    /// <summary>Rows counted by a key, noted most frequent first and folded past <see cref="Rows"/>.</summary>
    private sealed class Table
    {
        private readonly Dictionary<string, Split> _rows = new(StringComparer.Ordinal);

        private readonly bool _split;

        /// <param name="split">Whether the host and the links are told apart in a row's value.</param>
        public Table(bool split) => _split = split;

        public int Total => _rows.Values.Sum(one => one.Total);

        public void Count(string key, bool linked)
        {
            if (!_rows.TryGetValue(key, out var row))
                _rows[key] = row = new Split();

            row.Add(linked);
        }

        public void Note(RevitTestContext context, string label)
        {
            var ordered = _rows
                .OrderByDescending(pair => pair.Value.Total)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .ToList();

            if (ordered.Count == 0)
            {
                context.Note(label, "none");
                return;
            }

            foreach (var pair in ordered.Take(Rows))
            {
                context.Note(
                    label + ": " + pair.Key,
                    _split ? pair.Value.ToString() : pair.Value.Total.ToString(CultureInfo.InvariantCulture));
            }

            if (ordered.Count > Rows)
            {
                context.Note(
                    label + ": rows folded",
                    (ordered.Count - Rows).ToString(CultureInfo.InvariantCulture) + " rows holding "
                    + ordered.Skip(Rows).Sum(pair => pair.Value.Total).ToString(CultureInfo.InvariantCulture)
                    + " of " + Total.ToString(CultureInfo.InvariantCulture));
            }
        }
    }

    /// <summary>The first case's walk, and everything it counts.</summary>
    private sealed class ConnectorWalk
    {
        private static readonly (Type Type, Func<Element, ConnectorManager?> Manager)[] Owners =
        {
            (typeof(FamilyInstance), element => (element as FamilyInstance)?.MEPModel?.ConnectorManager),
            (typeof(MEPCurve), element => (element as MEPCurve)?.ConnectorManager),
            (typeof(FabricationPart), element => (element as FabricationPart)?.ConnectorManager),
            (typeof(MEPSystem), element => (element as MEPSystem)?.ConnectorManager),
        };

        public Split Documents { get; } = new();

        public int LinksNotLoaded { get; set; }

        public int LinksAlreadyWalked { get; set; }

        public int NestedLinks { get; set; }

        public SortedDictionary<string, int> ElementsByClass { get; } = new(StringComparer.Ordinal);

        public Split WithManager { get; } = new();

        public Split Connectors { get; } = new();

        public Split NotPhysical { get; } = new();

        public Split OwnedElsewhere { get; } = new();

        public List<string> CollectorsRefused { get; } = new();

        /// <summary>What the connector sets that were walked say they hold, by <c>ConnectorSet.Size</c>.</summary>
        public int SetSizes { get; private set; }

        /// <summary>The entries walking those same sets met, null entries included.</summary>
        public int SetEntriesMet { get; private set; }

        /// <summary>Sets walked whose size did not read, and so are left out of both counts above.</summary>
        public int SetsUnsized { get; private set; }

        public SortedDictionary<string, int> ManagersUnread { get; } = new(StringComparer.Ordinal);

        public SortedDictionary<string, int> TypeOrDomainUnread { get; } = new(StringComparer.Ordinal);

        public SortedDictionary<string, int> Unexpected { get; } = new(StringComparer.Ordinal);

        /// <summary>By type, domain and what Origin, IsConnected, CoordinateSystem and MEPSystem say.</summary>
        public Table Kinds { get; } = new(split: true);

        /// <summary>
        /// Connectors not physical, by the class and category of the element that owns them - and the element
        /// they were met through, when that is another - and who owns them.
        /// </summary>
        public Table Homes { get; } = new(split: true);

        /// <summary>
        /// Connectors not physical, by the same home, what their references are and whether they call
        /// themselves joined to the first.
        /// </summary>
        public Table References { get; } = new(split: true);

        /// <summary>Every connector, by whether its manager lists it unused, against what IsConnected says.</summary>
        public Table Unused { get; } = new(split: true);

        public void Walk(Document document, bool linked)
        {
            Documents.Add(linked);

            foreach (var (type, manager) in Owners)
            {
                FilteredElementCollector found;

                try
                {
                    found = new FilteredElementCollector(document).OfClass(type).WhereElementIsNotElementType();
                }
                catch (Autodesk.Revit.Exceptions.ArgumentException)
                {
                    // Documented for a class the filter does not support; no precedent here for asking
                    // it of MEPCurve or FabricationPart. Noted, the other classes are still walked, and
                    // the case fails on it after the walk: no connector of that class was counted.
                    if (!CollectorsRefused.Contains(type.Name))
                        CollectorsRefused.Add(type.Name);

                    continue;
                }

                foreach (var element in found)
                {
                    ConnectorWalkOf(element, type, manager, linked);
                }
            }
        }

        private void ConnectorWalkOf(Element element, Type asked, Func<Element, ConnectorManager?> read, bool linked)
        {
            Add(ElementsByClass, asked.Name);

            if (Ask(() => read(element), out var manager, "ConnectorManager", Unexpected) != Said.Reads)
            {
                Add(ManagersUnread, asked.Name);
                return;
            }

            if (manager is null)
                return;

            WithManager.Add(linked);

            var unused = new HashSet<string>(StringComparer.Ordinal);

            if (Ask(() => manager.UnusedConnectors, out var free, "UnusedConnectors", Unexpected) == Said.Reads && free is not null)
            {
                foreach (Connector connector in free)
                {
                    if (connector is not null && Identity(connector) is { } identity)
                        unused.Add(identity);
                }
            }

            var knowsUnused = free is not null;

            if (Ask(() => manager.Connectors, out var connectors, "Connectors", Unexpected) != Said.Reads)
            {
                Add(ManagersUnread, asked.Name);
                return;
            }

            // A set that reads as nothing hides no connector; only a refusal does.
            if (connectors is null)
                return;

            var sized = Ask(() => connectors.Size, out var size, "ConnectorSet.Size", Unexpected);
            var met = 0;

            var walkedClass = element.GetType().Name;
            var category = CategoryOf(element);

            foreach (Connector connector in connectors)
            {
                met++;

                if (connector is null)
                    continue;

                One(connector, element, walkedClass, category, linked, unused, knowsUnused);
            }

            if (sized == Said.Reads)
            {
                SetSizes += size;
                SetEntriesMet += met;
            }
            else
            {
                SetsUnsized++;
            }
        }

        private void One(
            Connector connector,
            Element element,
            string walkedClass,
            string category,
            bool linked,
            HashSet<string> unused,
            bool knowsUnused)
        {
            Connectors.Add(linked);

            var typeSaid = Ask(() => connector.ConnectorType, out var type, "ConnectorType", Unexpected);
            var domainSaid = Ask(() => connector.Domain, out var domain, "Domain", Unexpected);

            if (typeSaid != Said.Reads)
                Add(TypeOrDomainUnread, "ConnectorType " + Word(typeSaid));

            if (domainSaid != Said.Reads)
                Add(TypeOrDomainUnread, "Domain " + Word(domainSaid));

            // The raw value beside the flag, because the flag test production uses is also true of
            // EndSurface 17, Family 49, NonEnd 30 and AnyEnd 129: a row that said only "physical" could
            // not tell which of those a model holds.
            var physical = typeSaid == Said.Reads && (type & ConnectorType.Physical) != 0;
            var typeText = typeSaid == Said.Reads ? ((int)type).ToString(CultureInfo.InvariantCulture) : "unread";
            var domainText = domainSaid == Said.Reads ? ((int)domain).ToString(CultureInfo.InvariantCulture) : "unread";

            var origin = Ask(() => connector.Origin, out _, "Origin", Unexpected);
            var connected = Ask(() => connector.IsConnected, out var isConnected, "IsConnected", Unexpected);
            var frame = Ask(() => connector.CoordinateSystem, out _, "CoordinateSystem", Unexpected);
            var system = Ask(() => connector.MEPSystem, out var onSystem, "MEPSystem", Unexpected);
            var ownerSaid = Ask(() => connector.Owner, out var owner, "Owner", Unexpected);

            Kinds.Count(
                "type " + typeText + (physical ? " physical" : " not physical") + ", domain " + domainText
                + ": Origin " + Word(origin)
                + ", IsConnected " + Flag(connected, isConnected)
                + ", CoordinateSystem " + Word(frame)
                + ", MEPSystem " + (system == Said.Reads ? (onSystem is null ? "none" : "a system") : Word(system)),
                linked);

            var ownedBy = ownerSaid != Said.Reads
                ? "unread"
                : owner is null
                    ? "nothing"
                    : owner.Id.Value == element.Id.Value
                        ? "the element walked"
                        : "another element";

            if (ownedBy == "another element")
                OwnedElsewhere.Add(linked);

            var identity = Identity(connector);

            Unused.Count(
                "type " + typeText + ": listed unused " + (!knowsUnused || identity is null ? "not known" : unused.Contains(identity) ? "yes" : "no")
                + ", IsConnected " + Flag(connected, isConnected),
                linked);

            if (physical)
                return;

            NotPhysical.Add(linked);

            // Where the connector lives is its owner. A system lists connectors panels and devices own, and a
            // key built from the system alone would file a panel's logical connector as the circuit's - the
            // very part that tells a panel end from a device end.
            var home = ownedBy == "another element" && owner is not null
                ? owner.GetType().Name + " " + CategoryOf(owner) + ", met through " + walkedClass
                : walkedClass + " " + category;

            Homes.Count(home + ", type " + typeText + ", owned by " + ownedBy, linked);
            References.Count(home + ", type " + typeText + ": " + ReferencesOf(connector), linked);
        }

        /// <summary>How many references, of what types and owners, and whether the connector calls itself joined to the first.</summary>
        private string ReferencesOf(Connector connector)
        {
            var said = Ask(() => connector.AllRefs, out var refs, "AllRefs", Unexpected);

            if (said != Said.Reads || refs is null)
                return "AllRefs " + (said == Said.Reads ? "none" : Word(said));

            var types = new SortedSet<int>();
            var owners = new SortedSet<string>(StringComparer.Ordinal);
            Connector? first = null;
            var size = 0;

            foreach (Connector other in refs)
            {
                if (other is null)
                    continue;

                size++;
                first ??= other;

                if (Ask(() => other.ConnectorType, out var type, "ConnectorType of a reference", Unexpected) == Said.Reads)
                    types.Add((int)type);

                if (Ask(() => other.Owner, out var owner, "Owner of a reference", Unexpected) == Said.Reads)
                    owners.Add(owner is null ? "nothing" : owner.GetType().Name);
            }

            // Bucketed: an exact count per panel would make as many rows as a board has ways.
            var count = size >= 3 ? "3 or more" : size.ToString(CultureInfo.InvariantCulture);
            string joined;

            if (first is { } target)
            {
                var asked = Ask(() => connector.IsConnectedTo(target), out var yes, "IsConnectedTo", Unexpected);
                joined = Flag(asked, yes);
            }
            else
            {
                joined = "no reference to ask about";
            }

            return "references " + count + ", their types [" + string.Join(",", types) + "], owned by ["
                   + string.Join(",", owners) + "], IsConnectedTo the first " + joined;
        }

        /// <summary>A connector's identity across wrappers: its owner's id and its own, unique only within that owner.</summary>
        /// <remarks>Connector does not override Equals, so two wrappers of one connector are not equal by reference.</remarks>
        private string? Identity(Connector connector)
        {
            if (Ask(() => connector.Owner, out var owner, "Owner", Unexpected) != Said.Reads || owner is null)
                return null;

            if (Ask(() => connector.Id, out var id, "Id", Unexpected) != Said.Reads)
                return null;

            return owner.Id.Value.ToString(CultureInfo.InvariantCulture) + ":" + id.ToString(CultureInfo.InvariantCulture);
        }

        public void Note(RevitTestContext context, TimeSpan elapsed)
        {
            context.Note("census: documents walked", Documents.ToString());
            context.Note("census: link instances not loaded", LinksNotLoaded.ToString(CultureInfo.InvariantCulture));
            context.Note("census: link instances whose document was already walked", LinksAlreadyWalked.ToString(CultureInfo.InvariantCulture));
            context.Note("census: links inside links, not followed", NestedLinks.ToString(CultureInfo.InvariantCulture));
            context.Note("census: elements walked, by the class asked for", Listed(ElementsByClass));
            context.Note("census: elements with a connector manager", WithManager.ToString());
            context.Note("census: connector managers that could not be read, by the class asked for", Listed(ManagersUnread));
            context.Note("census: collectors Revit refused", CollectorsRefused.Count == 0 ? "none" : string.Join(", ", CollectorsRefused));
            context.Note(
                "census: connector sets, entries they say they hold against entries walked",
                SetSizes.ToString(CultureInfo.InvariantCulture) + " against " + SetEntriesMet.ToString(CultureInfo.InvariantCulture)
                + "; sets whose size did not read " + SetsUnsized.ToString(CultureInfo.InvariantCulture));
            context.Note("census: connectors walked", Connectors.ToString());
            context.Note("census: connectors not physical", NotPhysical.ToString());
            context.Note("census: connectors met through an element that does not own them", OwnedElsewhere.ToString());
            context.Note("census: type or domain unread", Listed(TypeOrDomainUnread));
            context.Note("census: exceptions other than InvalidOperationException itself", Listed(Unexpected));

            Kinds.Note(context, "connectors by what they answer");
            Homes.Note(context, "connectors not physical, by where they live");
            References.Note(context, "connectors not physical, by their references");
            Unused.Note(context, "connectors, listed unused against IsConnected");

            context.Note("census: elapsed, ms", Ms(elapsed));
        }
    }

    /// <summary>The second case's walk over the host's circuits.</summary>
    private sealed class EndWalk
    {
        public int Circuits { get; private set; }

        public int SpareOrSpace { get; private set; }

        public int DeviceVisits { get; private set; }

        /// <summary>What the element sets of the circuits walked say they hold, by <c>ElementSet.Size</c>.</summary>
        public int SetSizes { get; private set; }

        /// <summary>The entries walking those same sets met, null entries included.</summary>
        public int SetEntriesMet { get; private set; }

        /// <summary>Element sets walked whose size did not read, and so are left out of both counts above.</summary>
        public int SetsUnsized { get; private set; }

        public int Accounted { get; private set; }

        public int[] PanelWays { get; } = new int[ReaderLadder.PanelWays.Length];

        public int[] DeviceWays { get; } = new int[ReaderLadder.DeviceWays.Length];

        public List<string> Misplaced { get; } = new();

        public SortedDictionary<string, int> Unexpected { get; } = new(StringComparer.Ordinal);

        public Table Feeds { get; } = new(split: false);

        public Table Devices { get; } = new(split: false);

        public void Walk(Document document, CircuitHarvest harvest)
        {
            var described = new Dictionary<long, CircuitSnapshot>();

            foreach (var circuit in harvest.Described)
                described[circuit.Id.Value] = circuit;

            var systems = new FilteredElementCollector(document)
                .OfClass(typeof(ElectricalSystem))
                .Cast<ElectricalSystem>();

            foreach (var system in systems)
            {
                if (Ask(() => system.CircuitType, out var type, "CircuitType", Unexpected) == Said.Reads && type != CircuitType.Circuit)
                {
                    SpareOrSpace++;
                    continue;
                }

                Circuits++;

                described.TryGetValue(system.Id.Value, out var circuit);
                Ask(() => system.BaseEquipment, out var panel, "BaseEquipment", Unexpected);

                Feed(system, panel, circuit);

                if (Ask(() => system.Elements, out var elements, "Elements", Unexpected) != Said.Reads || elements is null)
                    continue;

                var sized = Ask(() => elements.Size, out var size, "ElementSet.Size", Unexpected);
                var met = 0;

                foreach (Element element in elements)
                {
                    met++;

                    if (element is not null)
                        Device(system, element, circuit);
                }

                if (sized == Said.Reads)
                {
                    SetSizes += size;
                    SetEntriesMet += met;
                }
                else
                {
                    SetsUnsized++;
                }
            }
        }

        private void Feed(ElectricalSystem system, FamilyInstance? panel, CircuitSnapshot? circuit)
        {
            var feedSaid = Ask(() => system.BaseEquipmentConnector, out var feed, "BaseEquipmentConnector", Unexpected);
            var panelManager = panel is null ? null : Manager(panel);

            string feedText;
            var owned = "not asked";
            var foundAgain = "not asked";
            var refsHold = "not asked";

            if (feedSaid != Said.Reads)
            {
                feedText = "feed " + Word(feedSaid);
            }
            else if (feed is null)
            {
                feedText = "no feed connector";
            }
            else
            {
                var typeSaid = Ask(() => feed.ConnectorType, out var type, "ConnectorType of the feed", Unexpected);
                feedText = "feed type " + (typeSaid == Said.Reads ? ((int)type).ToString(CultureInfo.InvariantCulture) : Word(typeSaid));

                var ownerSaid = Ask(() => feed.Owner, out var owner, "Owner of the feed", Unexpected);

                owned = ownerSaid != Said.Reads
                    ? Word(ownerSaid)
                    : owner is null
                        ? "nothing"
                        : panel is not null && owner.Id.Value == panel.Id.Value
                            ? "the panel"
                            : owner.Id.Value == system.Id.Value
                                ? "the circuit"
                                : "another element";

                // Connector.Id is unique only within its owner, so a lookup on the panel proves identity
                // only when the panel owns the feed.
                if (owned == "the panel" && panelManager is not null && Ask(() => feed.Id, out var id, "Id of the feed", Unexpected) == Said.Reads)
                {
                    var lookup = Ask(() => panelManager.Lookup(id), out var again, "Lookup", Unexpected);

                    foundAgain = lookup != Said.Reads
                        ? Word(lookup)
                        : again is null
                            ? "nothing"
                            : "type " + (Ask(() => again.ConnectorType, out var againType, "ConnectorType of the lookup", Unexpected) == Said.Reads
                                ? ((int)againType).ToString(CultureInfo.InvariantCulture)
                                : "unread");
                }

                if (panel is not null)
                    refsHold = HoldsAPhysicalConnectorOf(feed, panel);
            }

            var way = "not described";

            if (circuit is not null)
            {
                Accounted++;
                way = Place(() => ReaderLadder.Panel(system), circuit.Source.At, ReaderLadder.PanelWays, PanelWays, "panel " + circuit.Source.Owner.Value + " of circuit " + circuit.Id.Value);
            }

            Feeds.Count(
                feedText + ", owned by " + owned + ", found again on the panel " + foundAgain
                + ", its references hold a physical connector of the panel " + refsHold
                + "; the panel's physical electrical connectors " + PanelConnectors(system, panelManager)
                + "; point from " + way,
                linked: false);
        }

        private string HoldsAPhysicalConnectorOf(Connector feed, FamilyInstance panel)
        {
            var said = Ask(() => feed.AllRefs, out var refs, "AllRefs of the feed", Unexpected);

            if (said != Said.Reads || refs is null)
                return said == Said.Reads ? "no references" : Word(said);

            foreach (Connector other in refs)
            {
                if (other is null)
                    continue;

                if (Ask(() => other.Owner, out var owner, "Owner of a reference", Unexpected) != Said.Reads || owner is null || owner.Id.Value != panel.Id.Value)
                    continue;

                if (Ask(() => other.ConnectorType, out var type, "ConnectorType of a reference", Unexpected) == Said.Reads && (type & ConnectorType.Physical) != 0)
                    return "yes";
            }

            return "no";
        }

        /// <summary>Whether the panel's physical electrical connectors belong to this circuit, another system, or none.</summary>
        private string PanelConnectors(ElectricalSystem system, ConnectorManager? manager)
        {
            if (manager is null)
                return "not readable, no connector manager";

            var any = false;
            var onThis = false;
            var onAnother = false;
            var refused = false;

            foreach (Connector connector in manager.Connectors)
            {
                if (connector is null)
                    continue;

                if (Ask(() => connector.Domain, out var domain, "Domain of a panel connector", Unexpected) != Said.Reads || domain != Domain.DomainElectrical)
                    continue;

                if (Ask(() => connector.ConnectorType, out var type, "ConnectorType of a panel connector", Unexpected) != Said.Reads || (type & ConnectorType.Physical) == 0)
                    continue;

                any = true;

                var said = Ask(() => connector.MEPSystem, out var belongs, "MEPSystem of a panel connector", Unexpected);

                if (said != Said.Reads)
                    refused = true;
                else if (belongs is not null && belongs.Id.Value == system.Id.Value)
                    onThis = true;
                else if (belongs is not null)
                    onAnother = true;
            }

            if (!any)
                return "none at all";

            return onThis ? "on this circuit"
                : onAnother ? "on another system"
                : refused ? "refuse to name a system"
                : "on no system";
        }

        private void Device(ElectricalSystem system, Element element, CircuitSnapshot? circuit)
        {
            DeviceVisits++;

            var manager = Manager(element);
            var types = new SortedDictionary<int, int>();
            var onCircuit = "none";

            if (manager is not null)
            {
                foreach (Connector connector in manager.Connectors)
                {
                    if (connector is null)
                        continue;

                    if (Ask(() => connector.Domain, out var domain, "Domain of a device connector", Unexpected) != Said.Reads || domain != Domain.DomainElectrical)
                        continue;

                    if (Ask(() => connector.ConnectorType, out var type, "ConnectorType of a device connector", Unexpected) != Said.Reads)
                        continue;

                    types[(int)type] = types.TryGetValue((int)type, out var count) ? count + 1 : 1;

                    var said = Ask(() => connector.MEPSystem, out var belongs, "MEPSystem of a device connector", Unexpected);

                    if (said != Said.Reads)
                    {
                        if (onCircuit == "none")
                            onCircuit = "MEPSystem " + Word(said);

                        continue;
                    }

                    if (belongs is not null && belongs.Id.Value == system.Id.Value)
                        onCircuit = (type & ConnectorType.Physical) != 0 ? "physical" : "not physical";
                }
            }

            var way = circuit is null ? "circuit not described" : "dropped by the reader";

            if (circuit is not null && circuit.Devices.FirstOrDefault(one => one.Owner.Value == element.Id.Value) is { } end)
            {
                Accounted++;
                way = Place(() => ReaderLadder.Device(element, system), end.At, ReaderLadder.DeviceWays, DeviceWays, "device " + element.Id.Value + " of circuit " + circuit.Id.Value);
            }

            var connectors = manager is null
                ? "no connector manager"
                : types.Count == 0
                    ? "no electrical connector"
                    : "electrical connectors of type [" + string.Join(", ", types.Select(pair => pair.Key + " x" + pair.Value)) + "]";

            Devices.Count(connectors + "; on this circuit " + onCircuit + "; point from " + way, linked: false);
        }

        /// <summary>Asks the ladder, tallies the way, and records an end the way does not put where the reader did.</summary>
        private string Place(Func<ReaderLadder.Answer> ask, Point3 at, string[] names, int[] tally, string which)
        {
            var said = Ask(ask, out var answer, "the reader's ladder", Unexpected);

            if (said != Said.Reads)
            {
                Misplaced.Add(which + ": the ladder " + Word(said));
                return "a way that " + Word(said);
            }

            tally[answer.Way]++;

            if (!ReaderLadder.Same(answer.At, at))
                Misplaced.Add(which + ": " + names[answer.Way]);

            return names[answer.Way];
        }

        private ConnectorManager? Manager(Element element) =>
            Ask(() => (element as FamilyInstance)?.MEPModel?.ConnectorManager, out var manager, "ConnectorManager", Unexpected) == Said.Reads
                ? manager
                : null;

        public void Note(RevitTestContext context, CircuitHarvest? harvest, TimeSpan elapsed)
        {
            context.Note(
                "circuit ends: electrical systems",
                "circuits " + Circuits.ToString(CultureInfo.InvariantCulture) + ", spare or space " + SpareOrSpace.ToString(CultureInfo.InvariantCulture));

            if (harvest is not null)
            {
                context.Note(
                    "circuit ends: described by the reader",
                    "circuits " + harvest.Described.Count + " with " + harvest.Described.Sum(one => one.Devices.Count) + " devices; without a panel "
                    + harvest.WithoutPanel + ", without devices " + harvest.WithoutDevices + ", devices dropped " + harvest.DevicesSkipped);
            }

            context.Note("circuit ends: panel points, by the way that answered", Ways(ReaderLadder.PanelWays, PanelWays));
            context.Note("circuit ends: device points, by the way that answered", Ways(ReaderLadder.DeviceWays, DeviceWays));

            context.Note(
                "circuit ends: points the copy of the reader does not give",
                Misplaced.Count == 0 ? "none" : Misplaced.Count + " - " + string.Join("; ", Misplaced.Take(Rows)));

            context.Note(
                "circuit ends: element sets, entries they say they hold against entries walked",
                SetSizes.ToString(CultureInfo.InvariantCulture) + " against " + SetEntriesMet.ToString(CultureInfo.InvariantCulture)
                + "; sets whose size did not read " + SetsUnsized.ToString(CultureInfo.InvariantCulture));

            context.Note("circuit ends: exceptions other than InvalidOperationException itself", Listed(Unexpected));

            Feeds.Note(context, "circuit feeds");
            Devices.Note(context, "circuit devices");

            context.Note("circuit ends: elapsed, ms", Ms(elapsed));
        }

        private static string Ways(string[] names, int[] tally)
        {
            var answered = Enumerable.Range(0, names.Length)
                .Where(way => tally[way] > 0)
                .Select(way => names[way] + " " + tally[way].ToString(CultureInfo.InvariantCulture))
                .ToList();

            return answered.Count == 0 ? "none" : string.Join(", ", answered);
        }
    }
}
