using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;

namespace BHS.Revit.Engine.Tests;

/// <summary>
/// Whether the Revit database engine can be brought up inside a test process at all.
/// </summary>
/// <remarks>
/// <para>
/// <b>The first question, and it is asked on its own on purpose.</b> Everything this repository
/// wants from in-process testing - the cabling readers, the model settings on Extensible Storage,
/// the unit abstraction - rests on the engine starting. If it does not start on Revit 2024, then
/// the layer of automated checks for the oldest release we support does not exist, and that has to
/// be known before anything is built on it rather than after.
/// </para>
/// <para>
/// <b>What this is not.</b> It gives an <c>Application</c>, not a <c>UIApplication</c>: the ribbon,
/// the pump, the modal window, the journal, the channel and the binding of assemblies inside the
/// AppDomain of a running Revit are all out of reach here, and the sweep is neither replaced nor
/// shortened by it. A licensed Revit on the machine is still required, so the CI seam does not move
/// either: hosted CI has never started a Revit and still does not.
/// </para>
/// </remarks>
public class EngineComesUp : RevitApplicationTest
{
    [Test]
    public async Task TheEngineIsHere()
    {
        await Assert.That(Application).IsNotNull();
    }

    /// <summary>
    /// A document, because an engine that starts and cannot make one answers nothing worth asking.
    /// </summary>
    /// <remarks>
    /// Closed again rather than left open. A test process that accumulates documents is a test
    /// process whose later tests answer about a machine under memory pressure, which is the kind of
    /// flake that gets a suite switched off rather than fixed.
    /// </remarks>
    [Test]
    public async Task ADocumentCanBeMade()
    {
        var document = Application.NewProjectDocument(UnitSystem.Metric);

        try
        {
            await Assert.That(document).IsNotNull();
            await Assert.That(document.IsFamilyDocument).IsFalse();
        }
        finally
        {
            document?.Close(false);
        }
    }
}
