# BHS.Revit.Sdk - Custom MSBuild SDK for Revit

This is a custom MSBuild SDK designed to streamline the development of plugins and applications for Autodesk Revit. It
extends the standard `Microsoft.NET.Sdk` to provide seamless integration with Revit-specific Target Framework Monikers (
TFMs).

## Version Information

**Version:** 1.0.0

This is a fork of `BimHouse.Revit.Sdk`, taken into this repository so the build model can be
changed without touching a package that has other consumers. The upstream repository at
`..\BimHouse.Revit.Sdk` is read-only from here.

**Changes since the fork point:**

*   **Revit 2024 (`net48-revit2024`) is supported.** The dispatcher maps it to plain `net48`.
    The base SDK then resolves .NET Framework reference assemblies natively, so none of the
    `TargetFrameworkProfile` / `FrameworkPathOverride` machinery used by the older `BHS`
    solution is needed here - see "Why Revit 2024 needed no special machinery" below.
*   **Restore works.** It never did. `RevitTargetFrameworks`, which the restore dispatcher reads,
    was assigned nowhere, so both dispatch phases were skipped by their own conditions and
    `Restore` reported success without writing `project.assets.json`. Fixing that exposed a
    second fault right behind it: `Properties="TargetFrameworks=@(_StandardTfms, ';')"` is
    rejected by MSBuild with MSB4012, because an item list cannot be joined inside a task
    attribute. See "Restore dispatch" below.
*   **One TFM translation table.** `Sdk/targets/Revit.TfmMapping.targets` is the only place that
    knows a Revit TFM maps to a .NET one. The same table used to be copied into three files.
*   Publishing, deployment, `.addin` generation and TFM validation were empty two-line stubs in
    the fork; they are restored and adapted.
## Features

### 1. Custom Target Framework Monikers (TFMs)

The SDK introduces support for custom TFMs that explicitly declare both the .NET version and the target Revit version.

**Supported TFMs:**

| TFM | Revit | Compiles as |
|---|---|---|
| `net48-revit2024` | 2024 | `net48` |
| `net8.0-revit2025` | 2025 | `net8.0-windows` |
| `net8.0-revit2026` | 2026 | `net8.0-windows` |
| `net10.0-revit2027` | 2027 | `net10.0-windows` |

The custom moniker never reaches the compiler. An outer build reads the requested TFMs, translates
each one through `Sdk/targets/Revit.TfmMapping.targets`, and dispatches an inner build per entry
with a standard moniker the base SDK understands natively. `RevitVersion` is carried alongside in
`RevitInnerTfm`, and each version gets its own `obj\RevitNNNN\` and `bin\RevitNNNN\` subtree.

Adding a Revit version means adding one row to that mapping file and one row to the validation
table in `Revit.Validation.targets`. Nothing else in the SDK enumerates versions.

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
* Throws an error if there is a mismatch between the .NET version and the Revit version. The
  pairing is fixed by Autodesk: 2024 requires .NET Framework 4.8, 2025 and 2026 require .NET 8.0,
  2027 requires .NET 10.0. There is no free product of the two axes.
* Throws an error if a `-revit` TFM has no entry in the translation table, rather than dispatching
  an empty framework and building nothing.

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

## Bootstrap

The SDK is consumed from a feed inside this repository, `artifacts/feed`, declared in the root
`NuGet.config`. That config clears the machine-wide sources, so the previously published
`BimHouse.Revit.Sdk` on the local machine feed is deliberately invisible here.

MSBuild resolves an SDK package before any project is evaluated, so the SDK has to be in the feed
before anything else can restore:

```powershell
dotnet build build\BHS.Revit.Sdk\BHS.Revit.Sdk.csproj -c Release
```

`Pack` runs as part of that build and the `PushToRepoFeed` target copies the package into the feed.

If you rebuild the SDK without bumping its version, delete the extracted copy under
`%UserProfile%\.nuget\packages\bhs.revit.sdk` first, otherwise NuGet keeps serving the old one.

## How the dispatch works

### Two dispatchers, never both

The outer build is whichever evaluation was not started by us (`_IsInnerRevitBuild` unset).

* `TargetFramework` empty and `TargetFrameworks` set: `Microsoft.NET.Sdk.CrossTargeting.targets`.
* `TargetFramework` set: `Microsoft.NET.Sdk.targets`.

They define the same target names, so importing both would leave whichever was imported last
silently in charge. The import conditions are mutually exclusive on purpose.

### Hiding the custom moniker from the base SDK

`Sdk.targets` presets `TargetFrameworkIdentifier`, `TargetFrameworkVersion` and, for .NET 5+,
`TargetPlatformIdentifier` and `TargetPlatformVersion` whenever the outer build's `TargetFramework`
carries a `-revit` suffix. Both inference blocks in
`Microsoft.NET.TargetFrameworkInference.targets` are skipped when the properties they would compute
are already set. Without this, `dotnet build -f net8.0-revit2025` fails with NETSDK1139 ("target
platform identifier revit was not recognized") and `-f net10.0-revit2027` with NETSDK1140
("2027.0.0.0 is not a valid TargetPlatformVersion").

Rewriting `TargetFramework` instead is not an option: with `-f` it arrives as a global property, and
a project-level assignment cannot override one.

### Capture order

`RevitTargetFrameworks` is captured in `Sdk.targets`, not `Sdk.props`. Props are imported at the top
of the `.csproj`, before its body is evaluated, so `TargetFrameworks` is still empty there.

`TargetFramework` is read first and `TargetFrameworks` second. When one framework is asked for
explicitly the project still lists the others, and reading the list first made a `-f` build dispatch
all of them at once, which the base SDK rejects with NETSDK1046.

### Restore dispatch

NuGet restores a project as a whole, so `Restore` is intercepted and split:

1. Plain TFMs are handed back to the base SDK in one child invocation. It carries
   `_IsStandardRestore=true`, which switches that child out of outer-build mode. Without it the
   child re-enters this dispatcher and recurses until MSB4006.
2. Each Revit TFM gets its own child invocation with the translated framework, writing into
   `obj\RevitNNNN\`.

Set `RevitSdkDiagnostics=true` to have the dispatcher print what it captured and how it classified
each framework:

```powershell
dotnet restore -p:RevitSdkDiagnostics=true
```

### Language version on Revit 2024

`net48-revit2024` compiles as .NET Framework, where the compiler still defaults to C# 7.3. That
makes `Nullable=enable` fail with CS8630 on 2024 while the same project builds fine on 2025-2027.
The SDK therefore sets `LangVersion=latest` for Revit TFMs on .NET Framework.

It is a default, not an override: a project that sets `LangVersion` itself keeps its own value.

`latest` only unlocks the syntax. Features that need runtime support - records, `init` accessors,
index and range - still want a polyfill package such as `PolySharp` on net48.
### Why Revit 2024 needed no special machinery

The older `BHS` solution keeps the custom moniker as the real `TargetFramework` and patches
`TargetFrameworkProfile`, `FrameworkPathOverride` (via `ToolLocationHelper`) and
`AssetTargetFallback` to make .NET Framework resolution work anyway. This SDK translates instead:
the inner build compiles as plain `net48`, which the base SDK already resolves natively. None of
that machinery applies here.

What Revit 2024 does need is a .NET Framework 4.8 targeting pack on the build machine, either the
installed one or the `Microsoft.NETFramework.ReferenceAssemblies` package.

### Per-version output isolation

`AppendTargetFrameworkToOutputPath` is off for Revit TFMs, and two Revit versions can share an inner
moniker (2025 and 2026 are both `net8.0-windows`), so both intermediate and output directories are
redirected to `RevitNNNN` subtrees. `DefaultItemExcludes` gains the `obj` and `bin` roots: the base
SDK only excludes the current `BaseOutputPath` and `BaseIntermediateOutputPath`, so the sibling
versions' generated `AssemblyInfo.cs` would otherwise be picked up by the default glob and fail
with CS0579.

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
