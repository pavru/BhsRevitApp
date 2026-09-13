using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using BHS.MEP.Cabling.Routing;
using BHS.Revit.Testing;

namespace BHS.MEP.Cabling.Revit.Tests;

/// <summary>
/// How a circuit's connection is decided: its own parameter, else its panel's, else the project's.
/// </summary>
/// <remarks>
/// <para>
/// <b>A case that writes, and the only way this could be tested.</b> The linked test set does not
/// carry the parameter with values in it, and it should not have to: a model whose connection values
/// were set by hand for a test would test the model. So the case binds the parameter through the
/// production path, sets the values it needs, reads, and asserts - and the harness rolls every bit of
/// it back, binding included.
/// </para>
/// <para>
/// <b>It writes our shared parameter files as well</b>, into <c>%AppData%\BHS</c>, because binding
/// needs the definitions and <c>Install</c> opens the file rather than creating it - the first run of
/// the parameter scheme measured exactly that as two empty results. They are the same files the
/// Shared parameters command writes, with the same content, so this rewrites them rather than adds
/// anything.
/// </para>
/// </remarks>
public sealed class CircuitConnectionTests : IRevitTestSuite
{
    public string Name => "Cabling connection";

    public IEnumerable<RevitTestCase> Cases => new[]
    {
        new RevitTestCase(
            "a panel decides for its circuits, a circuit that says otherwise wins, and a typo is named",
            PanelDecidesUnlessTheCircuitSays,
            writes: true),
    };

    private static void PanelDecidesUnlessTheCircuitSays(RevitTestContext context)
    {
        var document = context.Document!;
        var application = context.Application.Application;

        // Which circuits the reader describes at all - a circuit it leaves out has no connection to
        // read, and asserting on one would be asserting on nothing.
        var described = new HashSet<long>(
            new CircuitReader().Read(document).Described.Select(one => one.Id.Value));

        var fed = new FilteredElementCollector(document)
            .OfClass(typeof(ElectricalSystem))
            .Cast<ElectricalSystem>()
            .Where(one => one.BaseEquipment is not null && described.Contains(one.Id.Value))
            .GroupBy(one => one.BaseEquipment.Id.Value)
            .OrderByDescending(group => group.Count())
            .FirstOrDefault()?
            .ToList();

        Skip.When(
            fed is null || fed.Count < 3,
            "the model this sweep opened has no panel feeding three described circuits, which the case needs");

        var scheme = new CablingParameters();
        scheme.Export(application);
        scheme.Install(document, application);

        // The connection parameter by id, and nothing wider. This asked "is anything missing" until the
        // apply phase declared two parameters whose category is known only at run time - which Install
        // without runtime categories rightly skips - and then failed on all four releases with a
        // sentence about a parameter that was in fact bound. An assertion should claim what its message
        // names.
        Expect.That(
            scheme.Missing(document).All(one => one.Id != CablingParameters.CircuitConnection),
            "binding through the production path left the connection parameter missing");

        var panel = fed![0].BaseEquipment;
        var inherits = fed[0];
        var overrides = fed[1];
        var mistyped = fed[2];

        using (var transaction = new Transaction(document, "BHS test: circuit connection"))
        {
            transaction.Start();

            Set(panel, CablingParameters.ConnectionAtJunctionBox);

            // Cleared explicitly: a model could already carry values, and this case asserts about the
            // values it set, not about whatever was there before.
            Set(inherits, string.Empty);
            Set(overrides, CablingParameters.ConnectionAtTerminal);
            Set(mistyped, "Junkbox");

            transaction.Commit();
        }

        var harvest = new CircuitReader(CircuitConnection.AtTerminal).Read(document);
        var read = harvest.Described.ToDictionary(one => one.Id.Value);

        Expect.That(
            read[inherits.Id.Value].Connection == CircuitConnection.AtJunctionBox,
            "circuit " + inherits.Id.Value + " is empty and its panel says JunctionBox, yet it reads as "
            + read[inherits.Id.Value].Connection);

        Expect.That(
            read[overrides.Id.Value].Connection == CircuitConnection.AtTerminal,
            "circuit " + overrides.Id.Value + " says Terminal on itself and was overruled by its panel");

        Expect.That(
            read[mistyped.Id.Value].Connection == CircuitConnection.AtTerminal,
            "a mistyped value on circuit " + mistyped.Id.Value + " was not answered with the project's default");

        Expect.That(
            harvest.UnreadableConnection.Any(one => one.Contains("Junkbox")),
            "the mistyped value was not named among the unreadable ones");

        context.Note("circuits on the panel used", fed.Count.ToString(CultureInfo.InvariantCulture));
        context.Note("unreadable values named", harvest.UnreadableConnection.Count.ToString(CultureInfo.InvariantCulture));
    }

    private static void Set(Element element, string value)
    {
        var parameter = element.get_Parameter(CablingParameters.CircuitConnection);

        Expect.That(
            parameter is not null && !parameter.IsReadOnly,
            "element " + element.Id.Value + " (" + element.Category?.Name + ") has no writable connection parameter after binding");

        parameter!.Set(value);
    }
}
