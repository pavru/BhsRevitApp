using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Linq;
using JetBrains.Annotations;
using Microsoft.Build.Framework;
using Task = Microsoft.Build.Utilities.Task;

namespace BHS.Revit.Sdk;

/// <summary>
///     MSBuild Task to generate a Revit .addin manifest file.
///     <para>
///     This task takes an <see cref="ITaskItem"/> representing the add-in definition and generates an XML manifest file 
///     required by Revit to load external applications and commands. The item's identity (ItemSpec) determines the 
///     output path of the generated .addin file.
///     </para>
///     <para>
///     The task validates the input metadata against Revit's manifest requirements and supports version-specific features 
///     (e.g., Revit 2025's <c>AllowLoadIntoExistingSession</c> and Revit 2026's <c>ManifestSettings</c>).
///     </para>
/// </summary>
/// <remarks>
///     Required metadata on the <see cref="AddInDefinition"/> item:
///     <list type="bullet">
///         <item><description><c>Assembly</c>: The full path to the add-in assembly (e.g., <c>MyPlugin.dll</c>).</description></item>
///         <item><description><c>FullClassName</c>: The fully qualified name of the class implementing <c>IExternalApplication</c> or <c>IExternalCommand</c>.</description></item>
///         <item><description><c>AddInId</c>: A unique GUID identifying the add-in.</description></item>
///         <item><description><c>VendorId</c>: A unique vendor identifier string (e.g., ADSK).</description></item>
///         <item><description><c>AddInName</c>: The display name of the add-in (required for <c>AddInType="Application"</c>). Maps to the <c>&lt;Name&gt;</c> or <c>&lt;Text&gt;</c> tag.</description></item>
///     </list>
///     Optional metadata includes <c>AddInType</c> (default is "Application"), <c>Description</c>, <c>VendorDescription</c>, 
///     <c>VisibilityMode</c>, <c>AvailabilityClassName</c>, <c>LanguageType</c>, <c>LongDescription</c>, <c>TooltipImage</c>, 
///     <c>LargeImage</c>, <c>Image</c>, <c>AllowLoadIntoExistingSession</c>, <c>UnifyInAddInManager</c>, <c>UseRevitContext</c>, 
///     and <c>ContextName</c>.
/// </remarks>
[PublicAPI]
public class GenerateRevitAddIn : Task
{
    /// <summary>
    ///     The MSBuild items that define the Revit add-ins this project declares.
    ///     Each item's Include (ItemSpec) is the destination file path of the .addin manifest; items sharing a path
    ///     are written into that one file as several <c>&lt;AddIn&gt;</c> elements, which is what Revit's format allows
    ///     and what an edition that is both an <c>Application</c> and a <c>DBApplication</c> needs.
    /// </summary>
    [Required]
    public ITaskItem[]? AddInDefinition { get; set; }

    /// <summary>
    ///     The target Revit version (e.g., "2024", "2025", "2026") extracted from the project's TargetFramework.
    ///     This is used to conditionally enable or disable version-specific manifest nodes and to issue appropriate warnings.
    /// </summary>
    [Required]
    public string RevitVersion { get; set; } = string.Empty;

    public override bool Execute()
    {
        try
        {
            if (AddInDefinition == null || AddInDefinition.Length == 0)
            {
                Log.LogError("AddInDefinition item is not provided.");
                return false;
            }

            // Grouped by destination, because a manifest is a file with a list in it and not a file
            // per add-in. An edition that offers both an Application - with its ribbon - and a
            // DBApplication for the part of itself that needs no window declares two, and Revit
            // reads them from one file. Declaring them separately would work too, and would mean
            // two files the deployment has to keep in step.
            var byFile = new Dictionary<string, List<ITaskItem>>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();

            foreach (var item in AddInDefinition)
            {
                if (!byFile.TryGetValue(item.ItemSpec, out var group))
                {
                    byFile[item.ItemSpec] = group = new List<ITaskItem>();
                    order.Add(item.ItemSpec);
                }

                group.Add(item);
            }

            var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var addInFilePath in order)
            {
                var elements = new List<XElement>();
                XElement? manifestSettings = null;

                foreach (var item in byFile[addInFilePath])
                {
                    if (!TryValidateInput(item, out var currentVersion, out var addInType))
                    {
                        return false;
                    }

                    // Two add-ins sharing an id are not a duplicate manifest entry: Revit files
                    // trust, isolation and the add-in manager by this value, and the host registry
                    // here is keyed by it as well. A collision would make one of them unreachable
                    // in a way that looks like it simply did not start.
                    var addInId = item.GetMetadata("AddInId");

                    if (ids.TryGetValue(addInId, out var taken))
                    {
                        Log.LogError($"AddInId '{addInId}' is declared twice: by '{taken}' and by " +
                                     $"'{item.GetMetadata("FullClassName")}'. Every add-in needs its own.");
                        return false;
                    }

                    ids[addInId] = item.GetMetadata("FullClassName");

                    elements.Add(GenerateAddInElement(item, currentVersion, addInType));

                    var settings = BuildManifestSettings(item, currentVersion);

                    if (settings == null)
                    {
                        continue;
                    }

                    if (manifestSettings == null)
                    {
                        manifestSettings = settings;
                    }
                    else if (!XNode.DeepEquals(manifestSettings, settings))
                    {
                        Log.LogError($"The add-ins written into '{addInFilePath}' ask for different ManifestSettings. " +
                                     "They share one manifest, so they share one isolation context; declare the same " +
                                     "settings on each, or put them in separate manifests.");
                        return false;
                    }
                }

                var root = new XElement("RevitAddIns", elements);

                if (manifestSettings != null)
                {
                    root.Add(manifestSettings);
                }

                var doc = new XDocument(new XDeclaration("1.0", "utf-8", "true"), root);

                var dir = Path.GetDirectoryName(addInFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                doc.Save(addInFilePath);
                Log.LogMessage(MessageImportance.Normal,
                    $"Successfully generated Revit AddIn manifest at: {addInFilePath} ({elements.Count} add-in(s))");
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.LogErrorFromException(ex);
            return false;
        }
    }

    private bool TryValidateInput(ITaskItem addInDefinition, out int currentVersion, out string addInType)
    {
        currentVersion = 0;
        addInType = "Application";

        if (!int.TryParse(RevitVersion, out currentVersion))
        {
            Log.LogError($"Invalid RevitVersion format: '{RevitVersion}'. It must be an integer.");
            return false;
        }

        var assembly = addInDefinition.GetMetadata("Assembly");
        var fullClassName = addInDefinition.GetMetadata("FullClassName");
        var addInId = addInDefinition.GetMetadata("AddInId");
        var vendorId = addInDefinition.GetMetadata("VendorId");
        var addInName = addInDefinition.GetMetadata("AddInName");

        var typeMetadata = addInDefinition.GetMetadata("AddInType");
        if (!string.IsNullOrEmpty(typeMetadata))
        {
            addInType = typeMetadata;
        }

        var isValid = true;

        if (string.IsNullOrWhiteSpace(assembly))
        {
            Log.LogError("The 'Assembly' attribute is missing or empty.");
            isValid = false;
        }

        if (string.IsNullOrWhiteSpace(fullClassName))
        {
            Log.LogError("The 'FullClassName' attribute is missing or empty.");
            isValid = false;
        }

        if (string.IsNullOrWhiteSpace(addInId))
        {
            Log.LogError("The 'AddInId' attribute is missing or empty.");
            isValid = false;
        }
        else if (!Guid.TryParse(addInId, out _))
        {
            Log.LogError($"The 'AddInId' attribute '{addInId}' is not a valid GUID.");
            isValid = false;
        }

        if (string.IsNullOrWhiteSpace(vendorId))
        {
            Log.LogError("The 'VendorId' attribute is missing or empty.");
            isValid = false;
        }

        if (string.IsNullOrWhiteSpace(addInName) && addInType.Equals("Application", StringComparison.OrdinalIgnoreCase))
        {
            Log.LogError("The 'AddInName' attribute is required for AddInType 'Application'.");
            isValid = false;
        }

        if (!addInType.Equals("Application", StringComparison.OrdinalIgnoreCase) &&
            !addInType.Equals("Command", StringComparison.OrdinalIgnoreCase) &&
            !addInType.Equals("DBApplication", StringComparison.OrdinalIgnoreCase))
        {
            Log.LogError($"The 'AddInType' attribute '{addInType}' is invalid. Valid values are: Application, Command, DBApplication.");
            isValid = false;
        }

        return isValid;
    }

    /// <summary>Builds one <c>&lt;AddIn&gt;</c> element. Manifest-wide settings are not its business.</summary>
    private XElement GenerateAddInElement(ITaskItem addInDefinition, int currentVersion, string addInType)
    {
        var assembly = addInDefinition.GetMetadata("Assembly");
        var fullClassName = addInDefinition.GetMetadata("FullClassName");
        var addInId = addInDefinition.GetMetadata("AddInId");
        var addInName = addInDefinition.GetMetadata("AddInName");
        var vendorId = addInDefinition.GetMetadata("VendorId");

        var description = addInDefinition.GetMetadata("Description");
        var vendorDescription = addInDefinition.GetMetadata("VendorDescription");

        var visibilityMode = addInDefinition.GetMetadata("VisibilityMode");
        var availabilityClassName = addInDefinition.GetMetadata("AvailabilityClassName");
        var languageType = addInDefinition.GetMetadata("LanguageType");
        var longDescription = addInDefinition.GetMetadata("LongDescription");
        var tooltipImage = addInDefinition.GetMetadata("TooltipImage");
        var largeImage = addInDefinition.GetMetadata("LargeImage");
        var image = addInDefinition.GetMetadata("Image");
        var allowLoadIntoExistingSession = addInDefinition.GetMetadata("AllowLoadIntoExistingSession");

        var addInElement = new XElement("AddIn", new XAttribute("Type", addInType),
            new XElement(addInType.Equals("Command", StringComparison.OrdinalIgnoreCase) ? "Text" : "Name", addInName),
            new XElement("Assembly", assembly),
            new XElement("AddInId", addInId),
            new XElement("FullClassName", fullClassName),
            new XElement("VendorId", vendorId)
        );

        if (!string.IsNullOrWhiteSpace(description)) addInElement.Add(new XElement("Description", description));
        if (!string.IsNullOrWhiteSpace(vendorDescription)) addInElement.Add(new XElement("VendorDescription", vendorDescription));
        if (!string.IsNullOrWhiteSpace(visibilityMode)) addInElement.Add(new XElement("VisibilityMode", visibilityMode));
        if (!string.IsNullOrWhiteSpace(availabilityClassName)) addInElement.Add(new XElement("AvailabilityClassName", availabilityClassName));
        if (!string.IsNullOrWhiteSpace(languageType)) addInElement.Add(new XElement("LanguageType", languageType));
        if (!string.IsNullOrWhiteSpace(longDescription)) addInElement.Add(new XElement("LongDescription", longDescription));
        if (!string.IsNullOrWhiteSpace(tooltipImage)) addInElement.Add(new XElement("TooltipImage", tooltipImage));
        if (!string.IsNullOrWhiteSpace(largeImage)) addInElement.Add(new XElement("LargeImage", largeImage));
        if (!string.IsNullOrWhiteSpace(image)) addInElement.Add(new XElement("Image", image));

        if (!string.IsNullOrWhiteSpace(allowLoadIntoExistingSession))
        {
            if (currentVersion >= 2025)
            {
                if (bool.TryParse(allowLoadIntoExistingSession, out var allowLoad))
                    addInElement.Add(new XElement("AllowLoadIntoExistingSession", allowLoad ? "true" : "false"));
                else
                    Log.LogWarning($"AllowLoadIntoExistingSession value '{allowLoadIntoExistingSession}' is not a valid boolean.");
            }
            else
            {
                Log.LogWarning("AllowLoadIntoExistingSession is only supported for Revit 2025 and newer. This setting will be ignored.");
            }
        }

        return addInElement;
    }

    /// <summary>
    ///     Builds the manifest-wide <c>&lt;ManifestSettings&gt;</c> element, or null when nothing asks for one.
    /// </summary>
    /// <remarks>
    ///     It sits beside the <c>&lt;AddIn&gt;</c> elements rather than inside one, so it describes the file and
    ///     everything declared in it. Two add-ins written into one manifest therefore share an isolation context,
    ///     which is the right answer for two halves of one edition and the reason they are worth keeping together.
    /// </remarks>
    private XElement? BuildManifestSettings(ITaskItem addInDefinition, int currentVersion)
    {
        var unifyInAddInManager = addInDefinition.GetMetadata("UnifyInAddInManager");
        var useRevitContext = addInDefinition.GetMetadata("UseRevitContext");
        var contextName = addInDefinition.GetMetadata("ContextName");

        if (currentVersion >= 2026)
        {
            var manifestSettingsElements = new List<XElement>();

            if (!string.IsNullOrWhiteSpace(unifyInAddInManager))
            {
                if (bool.TryParse(unifyInAddInManager, out var unify) && unify)
                    manifestSettingsElements.Add(new XElement("UnifyInAddInManager", "True"));
                else if (!bool.TryParse(unifyInAddInManager, out _))
                    Log.LogWarning($"UnifyInAddInManager value '{unifyInAddInManager}' is not a valid boolean.");
            }

            if (!string.IsNullOrWhiteSpace(useRevitContext))
            {
                if (bool.TryParse(useRevitContext, out var useRevit))
                    manifestSettingsElements.Add(new XElement("UseRevitContext", useRevit ? "True" : "False"));
                else
                    Log.LogWarning($"UseRevitContext value '{useRevitContext}' is not a valid boolean.");
            }

            if (!string.IsNullOrWhiteSpace(contextName))
                manifestSettingsElements.Add(new XElement("ContextName", contextName));

            if (manifestSettingsElements.Count > 0)
                return new XElement("ManifestSettings", manifestSettingsElements);
        }
        else if (!string.IsNullOrWhiteSpace(unifyInAddInManager) || !string.IsNullOrWhiteSpace(useRevitContext) || !string.IsNullOrWhiteSpace(contextName))
        {
            Log.LogWarning("ManifestSettings (UnifyInAddInManager, UseRevitContext, ContextName) are only supported for Revit 2026 and newer. These settings will be ignored.");
        }

        return null;
    }
}