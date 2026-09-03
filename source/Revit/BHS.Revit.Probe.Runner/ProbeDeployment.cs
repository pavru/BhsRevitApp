namespace BHS.Revit.Probe.Runner;

/// <summary>Names the build produces and this runner has to find again.</summary>
/// <remarks>
/// They come from the <c>RevitAddIn</c> item in the probe's project file and the vendor identity
/// in <c>Directory.Build.props</c>. Mirrored here rather than read back, and wrong values fail
/// visibly at the deployment check rather than quietly at launch.
/// </remarks>
internal static class ProbeDeployment
{
    public const string VendorId = "BimHouseSoftware";
    public const string AddInName = "BHS.Revit.Probe";
    public const string ManifestFileName = AddInName + ".addin";

    /// <summary>The probe's own <c>AddInId</c>, as written into the manifest by the SDK.</summary>
    public const string AddInId = "6f2e17ae-5ff7-45b2-bb8b-3482446e9a67";

    /// <summary>The probe's project, relative to the repository root.</summary>
    public const string ProjectPath = @"source\Revit\BHS.Revit.Probe\BHS.Revit.Probe.csproj";
}
