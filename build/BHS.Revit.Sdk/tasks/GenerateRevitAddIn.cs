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
    ///     An MSBuild item that defines the properties of the Revit AddIn.
    ///     The item's Include (ItemSpec) is used as the absolute or relative destination file path for the .addin manifest.
    /// </summary>
    [Required]
    public ITaskItem? AddInDefinition { get; set; }

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
            if (AddInDefinition == null)
            {
                Log.LogError("AddInDefinition item is not provided.");
                return false;
            }

            if (!TryValidateInput(AddInDefinition, out var currentVersion, out var addInType))
            {
                return false;
            }

            var doc = GenerateXml(AddInDefinition, currentVersion, addInType);

            var addInFilePath = AddInDefinition.ItemSpec;
            var dir = Path.GetDirectoryName(addInFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            doc.Save(addInFilePath);
            Log.LogMessage(MessageImportance.Normal, $"Successfully generated Revit AddIn manifest at: {addInFilePath}");

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

    private XDocument GenerateXml(ITaskItem addInDefinition, int currentVersion, string addInType)
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

        var unifyInAddInManager = addInDefinition.GetMetadata("UnifyInAddInManager");
        var useRevitContext = addInDefinition.GetMetadata("UseRevitContext");
        var contextName = addInDefinition.GetMetadata("ContextName");

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

        var revitAddInsElement = new XElement("RevitAddIns", addInElement);

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
                revitAddInsElement.Add(new XElement("ManifestSettings", manifestSettingsElements));
        }
        else if (!string.IsNullOrWhiteSpace(unifyInAddInManager) || !string.IsNullOrWhiteSpace(useRevitContext) || !string.IsNullOrWhiteSpace(contextName))
        {
            Log.LogWarning("ManifestSettings (UnifyInAddInManager, UseRevitContext, ContextName) are only supported for Revit 2026 and newer. These settings will be ignored.");
        }

        return new XDocument(new XDeclaration("1.0", "utf-8", "true"), revitAddInsElement);
    }
}