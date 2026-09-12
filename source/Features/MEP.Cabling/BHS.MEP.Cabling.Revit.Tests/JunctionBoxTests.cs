using System.Globalization;
using Autodesk.Revit.DB;
using BHS.MEP.Cabling.Routing;
using BHS.Revit.Testing;

namespace BHS.MEP.Cabling.Revit.Tests;

/// <summary>
/// That a junction box already in the model is recognised, and only when both signs agree.
/// </summary>
/// <remarks>
/// <para>
/// <b>A case that writes, for the same reason the connection case does:</b> the test models carry no
/// role on any type, and they should not - a model edited by hand to make a test pass tests the
/// model. So the case binds the parameter through the production path, marks one fitting type, reads,
/// and asserts. The harness rolls all of it back.
/// </para>
/// <para>
/// <b>What it can prove and what it cannot, said plainly.</b> The negative is strong: before the role
/// is set, nothing in the model is a box, so a reader that guessed from geometry would fail here. The
/// positive is weaker by construction - the case picks a fitting that is plainly joined to a carrier,
/// which restates half of the rule it is checking, so what it proves is that the reader agrees with
/// the model rather than that the rule is the right rule. The rule itself is the owner's.
/// </para>
/// </remarks>
public sealed class JunctionBoxTests : IRevitTestSuite
{
    public string Name => "Junction boxes";

    public IEnumerable<RevitTestCase> Cases => new[]
    {
        new RevitTestCase(
            "nothing is a junction box until a type says so, and then its instances are",
            TheRoleAndTheJointTogether,
            writes: true),
    };

    private static void TheRoleAndTheJointTogether(RevitTestContext context)
    {
        var document = context.Document!;
        var application = context.Application.Application;
        var catalogue = new CarrierCatalogue();

        var before = Read(document, catalogue);

        Expect.Same(0, before.Boxes.Count, "junction boxes found in a model where no type carries the role");
        Expect.Same(0, before.BoxesUnconnected, "elements called boxes by a role nobody has set");

        // A fitting of the host that is plainly joined to a carrier. Chosen with Revit's own answer
        // about the joint rather than with ours, so the case and the code under test do not agree by
        // construction about which fittings qualify.
        var joined = Fittings(document, catalogue).FirstOrDefault(one => JoinsACarrier(one, catalogue));

        Skip.When(
            joined is null,
            "the model this sweep opened has no fitting joined to a carrier, which the case needs");

        var type = joined!.GetTypeId();
        var siblings = Fittings(document, catalogue).Where(one => one.GetTypeId() == type).ToList();

        var scheme = new CablingParameters();
        scheme.Export(application);
        scheme.Install(document, application);

        using (var transaction = new Transaction(document, "BHS test: junction box role"))
        {
            transaction.Start();

            var parameter = document.GetElement(type)?.get_Parameter(CablingParameters.ElementRole);

            Expect.That(
                parameter is not null && !parameter.IsReadOnly,
                "the fitting type has no writable role parameter after binding through the production path");

            parameter!.Set(CablingParameters.JunctionBoxRole);
            transaction.Commit();
        }

        var after = Read(document, catalogue);

        Expect.That(
            after.Boxes.Count > 0,
            "a fitting type marked JunctionBox, with " + siblings.Count + " instance(s) joined to the structure, was recognised as none");

        Expect.Same(
            siblings.Count,
            after.Boxes.Count + after.BoxesUnconnected,
            "instances of the marked type, against the ones recognised as boxes plus the ones counted as joined to nothing");

        // The role was set in the host, and a link's types are the link's own. A box reported from a
        // link would mean the role was read from the wrong document.
        Expect.That(
            after.Boxes.All(box => !box.Id.IsLinked),
            "a box was reported from a link, where no type carries the role");

        // Still carriers: a real box is part of the structure and the route runs through it. Only our
        // own markers are taken out of the network.
        Expect.Same(
            before.Carriers.Count,
            after.Carriers.Count,
            "carriers before the role was set, against after - recognising a box must not remove it");

        context.Note("instances of the marked type", siblings.Count.ToString(CultureInfo.InvariantCulture));
        context.Note("recognised as boxes", after.Boxes.Count.ToString(CultureInfo.InvariantCulture));
        context.Note("joined to nothing", after.BoxesUnconnected.ToString(CultureInfo.InvariantCulture));
    }

    private static CablingSnapshot Read(Document document, CarrierCatalogue catalogue) =>
        CablingSnapshot.Build(document, Options, catalogue, version: 1);

    private static IEnumerable<Element> Fittings(Document document, CarrierCatalogue catalogue)
    {
        foreach (var category in catalogue.Categories)
        {
            var fittings = new FilteredElementCollector(document)
                .OfCategory(category)
                .WhereElementIsNotElementType()
                .OfClass(typeof(FamilyInstance));

            foreach (var element in fittings)
                yield return element;
        }
    }

    private static bool JoinsACarrier(Element element, CarrierCatalogue catalogue)
    {
        var manager = (element as FamilyInstance)?.MEPModel?.ConnectorManager;

        if (manager is null)
            return false;

        foreach (Connector connector in manager.Connectors)
        {
            if (connector is null || !connector.IsConnected)
                continue;

            foreach (Connector other in connector.AllRefs)
            {
                var owner = other?.Owner;

                if (owner is null || owner.Id == element.Id || owner.Category is null)
                    continue;

                if (catalogue.ClassOf((BuiltInCategory)owner.Category.Id.Value).Length > 0)
                    return true;
            }
        }

        return false;
    }

    private static RoutingOptions Options { get; } = new()
    {
        JoinTolerance = 0.5,
        MaxApproach = 10,
        AxisAlignedApproach = true,
    };
}
