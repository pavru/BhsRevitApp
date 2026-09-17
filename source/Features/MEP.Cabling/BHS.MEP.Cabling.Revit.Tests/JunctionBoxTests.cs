using System.Globalization;
using Autodesk.Revit.DB;
using BHS.MEP.Cabling.Routing;
using BHS.Revit.Common.Parameters;
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

        new RevitTestCase(
            "a box-role type whose instances carry a connector that refuses the question is read without throwing, and counted as joined to nothing",
            AConnectorThatRefusesTheQuestion,
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

    /// <summary>
    /// The rule that only a physical connector counts as joined, in the one model state where it decides
    /// anything.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why nothing else reaches it.</b> The connector census of 2026-09-16 found all 75 non-physical
    /// connectors of the linked set on circuits, on panels and on duct systems, and not one on a fitting.
    /// So on this model the filter can be taken out of the reader and every count stays the same: the
    /// rule guards a family nobody has put in front of it. It was written for one - an electrical
    /// equipment family carrying a logical or surface connector - and a defect waiting for a family is
    /// still a defect.
    /// </para>
    /// <para>
    /// <b>So the case builds that project, out of what this model already has.</b> The catalogue is the
    /// user's list, and a project that calls electrical equipment a carrier is an ordinary thing for it
    /// to say; the role goes on the equipment type, bound there through the production path with a
    /// runtime category, exactly as the indicator's own parameters are bound to the family a project
    /// chose. The reader then meets an element whose type says box and whose connectors include one that
    /// answers every question about a joint with an exception.
    /// </para>
    /// <para>
    /// <b>The element is chosen to be joined to nothing the catalogue collects</b>, by this suite's own
    /// answer rather than the reader's. Otherwise the walk over its connectors could return on a
    /// physical one before ever reaching the one that refuses, and the case would pass without asking
    /// the question it exists for.
    /// </para>
    /// <para>
    /// <b>The read is held, and the exception is the assertion.</b> Without the filter the reader comes
    /// out of Revit's own <c>InvalidOperationException</c> in the middle of a walk over the document, and
    /// the case would report "threw before it could assert" - a red with no name on it. Caught here, it
    /// has one.
    /// </para>
    /// </remarks>
    private static void AConnectorThatRefusesTheQuestion(RevitTestContext context)
    {
        var document = context.Document!;
        var application = context.Application.Application;

        // The defaults plus one more, named a class of its own: the class is an open string, and what
        // this project would be saying is that its equipment carries cable, not that it behaves like a
        // tray or like a conduit.
        var wide = new CarrierCatalogue(new Dictionary<BuiltInCategory, string>
        {
            [BuiltInCategory.OST_CableTray] = CarrierCatalogue.Tray,
            [BuiltInCategory.OST_CableTrayFitting] = CarrierCatalogue.Tray,
            [BuiltInCategory.OST_Conduit] = CarrierCatalogue.Conduit,
            [BuiltInCategory.OST_ConduitFitting] = CarrierCatalogue.Conduit,
            [Equipment] = "equipment",
        });

        var refusing = new FilteredElementCollector(document)
            .OfCategory(Equipment)
            .WhereElementIsNotElementType()
            .OfClass(typeof(FamilyInstance))
            .Cast<FamilyInstance>()
            .FirstOrDefault(one => Refusing(one) > 0 && !JoinsACarrier(one, wide));

        Skip.When(
            refusing is null,
            "the model this sweep opened holds no electrical equipment that carries a connector refusing the question and joins nothing the catalogue collects");

        context.Note(
            "connectors of the chosen equipment that refuse the question",
            Refusing(refusing!).ToString(CultureInfo.InvariantCulture));

        var before = Read(document, wide);

        Expect.Same(0, before.Boxes.Count, "junction boxes found with the wide catalogue, before any type carries the role");

        // Without this the case could pass about nothing: the rule is only ever asked of an element the
        // reader collected, and an element it dropped would never reach it.
        Expect.That(
            before.Carriers.Any(one => !one.Id.IsLinked && one.Id.Value == refusing!.Id.Value),
            "equipment " + refusing!.Id + " was not collected as a carrier by a catalogue that names its category, so the box rule is never asked about it");

        var scheme = new CablingParameters();
        scheme.Export(application);
        scheme.Install(document, application, new RuntimeCategories().Add(CablingParameters.ElementRole, Equipment));

        var type = refusing.GetTypeId();

        using (var transaction = new Transaction(document, "BHS test: a box role on equipment"))
        {
            transaction.Start();

            var parameter = document.GetElement(type)?.get_Parameter(CablingParameters.ElementRole);

            Expect.That(
                parameter is not null && !parameter.IsReadOnly,
                "the equipment type has no writable role parameter after binding it there through the production path");

            parameter!.Set(CablingParameters.JunctionBoxRole);
            transaction.Commit();
        }

        CablingSnapshot? after = null;
        Exception? thrown = null;

        try
        {
            after = Read(document, wide);
        }
        catch (Exception error)
        {
            thrown = error;
        }

        Expect.That(
            thrown is null,
            "reading a model where a type carrying the box role has instances with connectors that refuse the question threw "
            + thrown?.GetType().Name + ": " + thrown?.Message);

        Expect.That(
            after!.BoxesUnconnectedIds.Contains(refusing.Id.Value),
            "equipment " + refusing.Id + ", whose type says box and which joins nothing the catalogue collects, was not counted as a box joined to nothing");

        Expect.That(
            after.Boxes.All(box => box.Id.Value != refusing.Id.Value),
            "equipment " + refusing.Id + " was called a real box, though it joins nothing the catalogue collects");

        // Still a carrier, as the case above asserts of a fitting: being a box adds to an element and
        // takes nothing away.
        Expect.Same(
            before.Carriers.Count,
            after.Carriers.Count,
            "carriers read with the wide catalogue before the role was set, against after");
    }

    /// <summary>The category a panel is in, which is where this model's non-physical connectors live.</summary>
    private const BuiltInCategory Equipment = BuiltInCategory.OST_ElectricalEquipment;

    /// <summary>How many of this element's connectors would refuse a question about a joint.</summary>
    private static int Refusing(Element element)
    {
        var manager = (element as FamilyInstance)?.MEPModel?.ConnectorManager;

        if (manager is null)
            return 0;

        var refusing = 0;

        foreach (Connector connector in manager.Connectors)
        {
            if (connector is not null && (connector.ConnectorType & ConnectorType.Physical) == 0)
                refusing++;
        }

        return refusing;
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
            // Physical only, as the reader asks: a surface or logical connector refuses IsConnected.
            if (connector is null || (connector.ConnectorType & ConnectorType.Physical) == 0 || !connector.IsConnected)
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
