# BHS.Revit.Sdk - Custom MSBuild SDK for Revit

This is a custom MSBuild SDK designed to streamline the development of plugins and applications for Autodesk Revit. It
extends the standard `Microsoft.NET.Sdk` to provide seamless integration with Revit-specific Target Framework Monikers (
TFMs).

## Version Information

**Version:** 1.0.40

**Recent Changes:**
*   **v1.0.40**:
    *   **Critical IDE Compatibility Fix**: Refactored the Outer Build evaluation logic to ensure `TargetFrameworks` flows unmodified into the base SDK. This completely resolves the issue where Rider/Visual Studio could not "see" the custom TFMs in the UI and only evaluated the fallback framework.
    *   Added dedicated Outer-Loop dispatchers in both `Microsoft.NET.Sdk.targets` (for single-targeting) and `Microsoft.NET.Sdk.CrossTargeting.targets` (for multi-targeting).
*   **v1.0.39**:
    *   Added bypass logic for `ProcessFrameworkReferences` in `Microsoft.NET.Sdk.FrameworkReferenceResolution.targets`.
*   **v1.0.35**:
    *   Added bypass logic for `ProcessFrameworkReferences`.
*   **v1.0.34**:
    *   Added `ProcessFrameworkReferences` wrapper.
*   **v1.0.33**:
    *   Moved `ResolveFrameworkReferences` bypass logic to a dedicated file `Microsoft.NET.Sdk.FrameworkReferenceResolution.targets` to align with base SDK structure.
*   **v1.0.31**: 
    *   **Added Publishing and Deployment:** Introduced `PublishRevitSpecificPackage` and `PublishRevitCommonPackage` targets to create distributable packages.
    *   **Added Deployment Targets:** Introduced `DeployRevitSpecificPackage` and `DeployRevitCommonPackage` to install packages locally for testing.
    *   **Flexible Package Configuration:** Package structure is now determined by the presence of the `<RevitVersion>` property.
    *   **Simplified Deployment Scopes:** The `<RevitDeploy>` property now accepts simple `System` and `Local` values.
    *   **Enhanced Validation:** Added warnings for missing `VendorId` and `PackageName` during publishing.
    *   **Comprehensive Add-In Generation:** The `GenerateRevitAddIn` task now supports a wide range of `.addin` manifest properties, including version-specific features for Revit 2025 and 2026.

## Features

### 1. Custom Target Framework Monikers (TFMs)

The SDK introduces support for custom TFMs that explicitly declare both the .NET version and the target Revit version.

**Supported TFMs:**

* `net8.0-revit2025`
* `net8.0-revit2026`
* `net10.0-revit2027`

By using these TFMs, the SDK automatically determines the correct `.NETCoreApp` version, `TargetPlatformIdentifier` (
`revit`), and `TargetPlatformVersion`.

### 2. IDE Contexts & Preprocessor Directives

When a Revit TFM is used, the SDK automatically generates preprocessor constants corresponding to the target
environment.
If you target Revit 2026 (`net8.0-revit2026`), the following constants will be defined:

* `REVIT`
* `REVIT2026`
* `REVIT2024_OR_GREATER`
* `REVIT2025_OR_GREATER`
* `REVIT2026_OR_GREATER`

This functionality is powered by a custom MSBuild task shipped within the SDK, mimicking the behavior of standard
`.NETCoreApp` constants.

### 3. Validation

The SDK ensures configuration consistency:

* Throws an error if an unsupported Revit version is specified in the TFM.
* Throws an error if there is a mismatch between the .NET version and the Revit version (e.g., Revit 2025 requires .NET
  8.0, Revit 2027 requires .NET 10.0).

### 4. WPF, WinForms, and WinUI Support

The SDK includes robust workarounds for .NET 8+ platform validation checks.

* You can use `<UseWPF>true</UseWPF>`, `<UseWindowsForms>true</UseWindowsForms>`, or `<UseWinUI>true</UseWinUI>` without
  encountering `NETSDK1136` errors.
* The SDK temporarily masks the platform as `Windows` during critical MSBuild and NuGet validation steps (
  `ResolvePackageAssets`), allowing UI framework dependencies to resolve correctly while keeping your project targeting
  the `revit` platform.
* Dynamically generates `AssetTargetFallback` to ensure NuGet packages targeting standard Windows versions are restored
  successfully.

### 5. Blocked Warnings and Errors

The SDK explicitly bypasses or suppresses certain strict validations from the base .NET SDK to allow the custom `revit`
platform to function correctly:

* **NETSDK1139**: Bypassed by properly registering `revit` as a supported `TargetPlatformIdentifier`.
* **NETSDK1136**: Bypassed for UI frameworks (WPF/WinForms) by internally indicating compatibility with Windows Desktop
  components and wrapping `_CheckForInvalidWindowsDesktopTargetingConfiguration`.
* **NU1202**: Bypassed by suppressing the warning `NoWarn="$(NoWarn);NU1202"` to silence NuGet compatibility warnings
  for packages targeting specific platforms (like `net8.0-windows7.0`), combined with custom `ResolvePackageAssets`
  targeting wrappers.
* **NU1701**: Suppressed globally (`NoWarn="$(NoWarn);NU1701"`) to prevent warnings when resolving old packages inside
  the new TFM structure.

### 6. Implicit Package Management (Nice3point Revit API)

The SDK automatically manages references to the `Nice3point.Revit.Api.*` packages based on project properties.

* The package versions automatically align with the target Revit version specified in the TFM.

**Available Properties:**

* `<UseRevitApi>true</UseRevitApi>` *(Default: true)* - Includes `Nice3point.Revit.Api.RevitAPI`
* `<UseRevitApiUi>true</UseRevitApiUi>` - Includes `Nice3point.Revit.Api.RevitAPIUI`
* `<UseRevitAdWindows>true</UseRevitAdWindows>` - Includes `Nice3point.Revit.Api.AdWindows`
* `<UseRevitApiIfc>true</UseRevitApiIfc>` - Includes `Nice3point.Revit.Api.RevitAPIIFC`
* `<UseRevitUiFramework>true</UseRevitUiFramework>` - Includes `Nice3point.Revit.Api.UIFramework`
* `<UserRevitApiMacros>true</UserRevitApiMacros>` - Includes `Nice3point.Revit.Api.RevitAPIMacros`
* `<UseRevitAddInUtility>true</UseRevitAddInUtility>` - Includes `Nice3point.Revit.Api.RevitAddInUtility`
* `<UserRevitTUnit>true</UserRevitTUnit>` - Includes `Nice3point.TUnit.Revit`

### 7. Revit AddIn Manifest Generation

The SDK can automatically generate `.addin` manifest files for your Revit application or command. This task runs after a successful build and places the generated `.addin` file in the `$(TargetDir)addin\` subdirectory.

To configure the `.addin` file generation, add a `RevitAddIn` item to your `.csproj` file. These items are hidden from the Solution Explorer by default.

**Example Usage:**

```xml
<ItemGroup>
  <RevitAddIn Include="$(AssemblyName).addin">
    <FullClassName>YourNamespace.YourApplicationOrCommandClass</FullClassName>
    <AddInId>PUT-YOUR-UNIQUE-GUID-HERE</AddInId>
    <VendorId>YourVendorID</VendorId>
    <!-- Optional: Display name for the AddIn. Defaults to AssemblyName. -->
    <AddInName>My Awesome Revit Plugin</AddInName>
    
    <!-- Optional Common Elements -->
    <VisibilityMode>AlwaysVisible</VisibilityMode>
    <AvailabilityClassName>YourNamespace.CommandAvailabilityClass</AvailabilityClassName>
    <LanguageType>English_USA</LanguageType>
    <LongDescription>A very long description for the addin.</LongDescription>
    <TooltipImage>path\to\tooltip.png</TooltipImage>
    <LargeImage>path\to\large_image.png</LargeImage>
    <Image>path\to\small_image.png</Image>

    <!-- Optional (Revit 2025+): Allow loading add-in when Revit is already running -->
    <AllowLoadIntoExistingSession>true</AllowLoadIntoExistingSession>

    <!-- Optional (Revit 2026+): If true, groups all add-ins in this manifest into a single entry in the Add-In Manager. -->
    <UnifyInAddInManager>true</UnifyInAddInManager>
    <!-- Optional (Revit 2026+): If false, runs the add-in in a separate Assembly load context. Default is true. -->
    <UseRevitContext>false</UseRevitContext>
    <!-- Optional (Revit 2026+): A custom ContextName for the add-in when UseRevitContext is false. -->
    <ContextName>MyIsolatedContext</ContextName>
  </RevitAddIn>
</ItemGroup>
```

### 8. Publishing and Deployment

The SDK provides built-in targets to package your add-in for distribution (`Publish`) and to install it locally for testing (`Deploy`).

#### Publishing

To enable publishing, set the `<PublishRevitAddIn>true</PublishRevitAddIn>` property in your project. The SDK provides two distinct publishing modes.

**1. Revit-Specific Package**

This mode is activated when the `<RevitVersion>` property is defined in the project. It's designed for add-ins compiled against a specific Revit API.

*   **Condition:** `<RevitVersion>` is defined AND `<PublishRevitAddIn>` is `true`.
*   **Action:** The `PublishRevitSpecificPackage` target runs, creating a version-specific package. The `.addin` manifest is generated with a relative path and copied to the root of the version folder.
*   **Structure:**
    ```text
    publish/
    └── [RevitVersion]/
        ├── [AddInName].addin
        └── [VendorId]/
            └── [AddInName]/
                ├── Lib/
                ├── Resources/
                └── Content/
    ```

**2. Revit-Common Package**

This mode is activated when the `<RevitVersion>` property is **not** defined. It's for creating a bundle of shared libraries or resources that are version-independent.

*   **Condition:** `<RevitVersion>` is NOT defined AND `<PublishRevitAddIn>` is `true`.
*   **Action:** The `PublishRevitCommonPackage` target runs. **No `.addin` file is generated or copied.** The package structure is defined by the `<RevitVendorId>` and `<RevitPackageName>` properties.
*   **Properties:**
    ```xml
    <PropertyGroup>
      <RevitVendorId>MyCompany</RevitVendorId>
      <RevitPackageName>MySharedLibrary</RevitPackageName>
    </PropertyGroup>
    ```
*   **Structure:**
    ```text
    publish/
    └── [RevitVendorId]/
        └── [RevitPackageName]/
            ├── Lib/
            ├── Resources/
            └── Content/
    ```

**Content Item Groups:**

Use these item groups to include files in your package:

```xml
<ItemGroup>
  <!-- Files for the Lib folder (automatically included) -->
  
  <!-- Files for the Resources folder -->
  <RevitAddInResources Include="Images\**\*.*" />
  
  <!-- Files for the Content folder -->
  <RevitAddInContent Include="RevitFiles\*.rfa" />
</ItemGroup>
```

#### Deployment

To automatically copy a package, set the `<RevitDeploy>` property. If this property is not set or is empty, deployment is disabled.

**1. Revit-Specific Package Deployment**

*   **Condition:** `<RevitVersion>` is defined AND `<RevitDeploy>` is set.
*   **Action:** The `DeployRevitSpecificPackage` target runs.
*   **Scopes (`RevitDeploy` property):**
    *   `System`: Installs for all users in `%ProgramData%\Autodesk\Revit\Addins\[RevitVersion]\`.
    *   `Local` (Default if not specified): Installs for the current user in `%AppData%\Autodesk\Revit\Addins\[RevitVersion]\`.

**2. Revit-Common Package Deployment**

*   **Condition:** `<RevitVersion>` is NOT defined AND `<RevitDeploy>` is set.
*   **Action:** The `DeployRevitCommonPackage` target runs.
*   **Scopes (`RevitDeploy` property):**
    *   `System`: Installs for all users in `%ProgramFiles%\[RevitVendorId]\[RevitPackageName]\`.
    *   `Local` (Default if not specified): Installs for the current user in `%LocalAppData%\[RevitVendorId]\[RevitPackageName]\`.

**Example Deployment Configuration:**

```xml
<PropertyGroup>
  <!-- Enable Publishing -->
  <PublishRevitAddIn>true</PublishRevitAddIn>
  <!-- Enable and configure Deployment -->
  <RevitDeploy>System</RevitDeploy>
</PropertyGroup>
```

## Usage

In your project file (`.csproj`), specify the SDK at the top and set your desired custom TFM:

```xml
<Project Sdk="BHS.Revit.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0-revit2025</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    
    <!-- Optional UI support -->
    <UseWPF>true</UseWPF>

    <!-- Optional API Packages (UseRevitApi is true by default) -->
    <UseRevitApiUi>true</UseRevitApiUi>
  </PropertyGroup>

</Project>
```

To support multiple Revit versions simultaneously (Multi-Targeting):

```xml
<Project Sdk="BHS.Revit.Sdk">
  <PropertyGroup>
    <TargetFrameworks>net8.0-revit2025;net8.0-revit2026;net10.0-revit2027</TargetFrameworks>
  </PropertyGroup>
</Project>
```
