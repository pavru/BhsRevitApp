# BHS.Revit.Sdk - Custom MSBuild SDK for Revit

A custom MSBuild SDK for Autodesk Revit add-ins. It extends `Microsoft.NET.Sdk` with target
framework monikers that name both the .NET version and the Revit release, implicit Revit API
package references, `.addin` manifest generation, and publishing.

## Version Information

**Version:** 1.2.3

Bump this on every change, and update the `Sdk="BHS.Revit.Sdk/<version>"` attribute in the
projects under `source/` with it. The package carries an MSBuild task assembly, so any process
that has evaluated a Revit project - the IDE included - holds `BHS.Revit.Sdk.dll` open and the
extracted package cannot be replaced in place. A new version number sidesteps that entirely.

This is a fork of `BimHouse.Revit.Sdk`, taken into this repository so the build model can be
changed without touching a package that has other consumers. The upstream repository at
`..\BimHouse.Revit.Sdk` is read-only from here.

**Changes since the fork point:**

*   **The moniker is no longer translated.** Up to 1.0.1 - and in the upstream to this day - an
    outer build rewrote `net48-revit2024` into plain `net48` and dispatched the real work into a
    child MSBuild call. That works from the command line and is invisible to an IDE: Rider showed
    these projects with a single `.NETFramework,Version=v0.0` framework, no source files and a
    failed restore, because everything it needs happens inside a child build it never sees.
    The moniker is now the real `TargetFramework` and there is no dispatcher at all - see
    "How the moniker works".
*   **Revit 2024 (`net48-revit2024`) is supported.** It is the reason the profile and
    `FrameworkPathOverride` machinery exists: .NETFramework has no target platform, so the Revit
    part of the moniker has to be carried as the framework profile.
*   **`revit` is a real target platform on the .NET axis.** The release year is the platform
    version, which is what makes `net8.0-revit2025` and `net8.0-revit2026` two frameworks rather
    than two names for one. Carrying the release nowhere in the identity cost Revit 2026 its place
    in the IDE's project model entirely - see "Custom Target Framework Monikers" below.
*   **Restore works.** It never did. `RevitTargetFrameworks`, which the old restore dispatcher
    read, was assigned nowhere, so both dispatch phases were skipped by their own conditions and
    `Restore` reported success without writing `project.assets.json`. The dispatcher it belonged
    to is gone; restore is now the base SDK's own.
*   Publishing, deployment, `.addin` generation and TFM validation were empty two-line stubs in
    the fork; they are restored and adapted.

## Features

### 1. Custom Target Framework Monikers (TFMs)

The SDK introduces TFMs that declare both the .NET version and the target Revit release.

**Supported TFMs:**

| TFM | Revit | Built on | The release is carried as | Reference assemblies from |
|---|---|---|---|---|
| `net48-revit2024` | 2024 | .NET Framework 4.8 | framework profile `revit2024` | `net48` |
| `net8.0-revit2025` | 2025 | .NET 8 | platform `revit` 2025.0 | `net8.0-windows7.0` |
| `net8.0-revit2026` | 2026 | .NET 8 | platform `revit` 2026.0 | `net8.0-windows7.0` |
| `net10.0-revit2027` | 2027 | .NET 10 | platform `revit` 2027.0 | `net10.0-windows7.0` |

The moniker reaches the compiler unchanged. It is what the assembly is built as, what NuGet files
the project under, and what names the output directory: `bin\Debug\net48-revit2024\`.

The fourth column is the part that is easy to get wrong. The Revit release has to be *part of the
framework identity*, not merely part of its name, because that identity is what an IDE keys its
project model on. While the .NET axis declared platform `windows`, Revit 2025 and 2026 both came
out as `.NETCoreApp,Version=v8.0 / windows10.0.17763` - the same framework twice - and Rider kept
the first and dropped Revit 2026 from the solution altogether.

Adding a Revit release is one row in `RevitSupportedReleases` in `Revit.Identity.targets`, written
as `<year>=<plain moniker>`. The validation table and the list of declared platform versions are
both derived from it; the framework identity is derived from the moniker itself.

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

The SDK ensures configuration consistency, before the build and before restore collects packages:

* Throws an error if an unsupported Revit release is named in the TFM. Restore has to be covered
  too, because the implicit Revit API references are versioned from the release in the moniker, so
  an unknown release would otherwise surface as NU1102 on a package that was never going to exist.
* Throws an error if the .NET version and the Revit release disagree. The pairing is fixed by
  Autodesk: 2024 requires .NET Framework 4.8, 2025 and 2026 require .NET 8.0, 2027 requires
  .NET 10.0. There is no free product of the two axes.

### 4. WPF, WinForms, and WinUI Support

`<UseWPF>` and `<UseWindowsForms>` work on Revit TFMs, XAML markup compilation included. On
`net48-revit2024` the question does not arise: .NET Framework carries WPF and WinForms in the box.

On the .NET axis it takes two things, because the target platform is `revit` rather than `windows`.
`ImportWindowsDesktopTargets` is switched on for exactly the projects that ask for WPF or WinForms
- without it the markup compiler never runs and every code-behind fails on `InitializeComponent`,
and with it on for everyone else the build warns NETSDK1106. And the base SDK's check that the
platform must be Windows is switched off in `Revit.Platform.targets`; Revit-side code is Windows
desktop by definition, and the check has no way to know that.

`<UseWinUI>` gets defaults for `TargetPlatformMinVersion`, `RuntimeIdentifiers` and
`EnableMsixTooling`, but it is untested on a Revit TFM. Note that Windows App SDK needs .NET 6 or
later, so WinUI is not available on Revit 2024 at all.

### 5. Warnings the SDK suppresses

Three, all of them consequences of the design rather than anything a project can act on:

* **NU1701** - a package was restored through `AssetTargetFallback`.
* **NU1702** - a project reference was resolved through it.
* **CA1418** - `revit` is not a platform name the analyser has heard of. It fires on the
  `[SupportedOSPlatform("revit2026.0")]` attribute the SDK generates for us, once per framework.
  Suppressing it also stops CA1416 reasoning about Windows-only APIs, which costs nothing here:
  everything Revit-side is Windows.

Nobody publishes packages for `net48-revit2024`, so the fallback is how a Revit TFM consumes
anything at all. See "How packages resolve" for why the NuGet pair is suppressed unconditionally
rather than only on Revit frameworks.

**NU1202 is not suppressed**, although the previous design suppressed it. It means a package has
nothing for this framework, and that is worth failing on.

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

## How the moniker works

There is no dispatcher. `Sdk/targets/Revit.Identity.targets` fills in everything the base SDK
would otherwise have to infer from the moniker, and then the base SDK does the rest: cross-target
dispatch, restore, compilation, design-time builds. That is the whole design.

### Where the identity is computed

`Revit.Identity.targets` is imported from `Sdk.targets` *before* the base `Sdk.targets`. That is
the earliest point at which `TargetFramework` is known however it was supplied - as a global
property from the base cross-targeting dispatcher or from `dotnet build -f`, or from the project
body, which the props files are evaluated too early to see. One import covers all three.

It sets `TargetFrameworkIdentifier` and `TargetFrameworkVersion` from the plain part of the
moniker, and then, per axis:

* **.NETFramework** - `TargetFrameworkProfile` becomes `revit2024`. That is not cosmetic:
  `TargetFrameworkMoniker` is built from identifier, version and profile, and it is what NuGet
  writes into the assets file. Leave the profile out and restore files the project under plain
  `net48` while the build asks for `net48-revit2024`, which is NETSDK1005. A profile normally
  implies a reference-assembly directory, and there is no `...\.NETFramework\v4.8\Profile\revit2024`,
  so `FrameworkPathOverride` points resolution at the plain v4.8 directory and
  `EnableFrameworkPathOverride=false` stops the base SDK computing its own.
* **.NETCoreApp** - `TargetPlatformIdentifier` becomes `revit`, versioned by the release year.
  There is no profile to use here, and the release has to live somewhere in the identity or two
  releases on one .NET version become the same framework. Declaring a platform of our own means
  declaring the things a Windows platform would have implied:

  | Property | Why |
  |---|---|
  | `TargetPlatformSupported` | the base SDK knows Windows and the workload platforms, and errors NETSDK1139 on anything else |
  | `SdkSupportedTargetPlatformVersion` | the sanctioned way to say which platform versions exist; without it NETSDK1140 rejects the release year |
  | `ImportWindowsDesktopTargets` | brings in the WPF targets, for projects that ask for WPF or WinForms |

Both inference blocks in `Microsoft.NET.TargetFrameworkInference.targets` are skipped when the
properties they would compute are already set. Without that, `net8.0-revit2025` fails with
NETSDK1139 ("target platform identifier revit was not recognized") and `net10.0-revit2027` with
NETSDK1140, because `2027` is read as a platform version nobody declared.

Two base-SDK targets are switched off for Revit frameworks in `Revit.Platform.targets`: the check
that WPF needs a Windows platform, and the automatic platform preprocessor constants, which would
otherwise put a second `REVIT2026_0` family alongside the `REVIT2026` one the SDK's own task
generates.

### How packages resolve

`AssetTargetFallback` gains the plain moniker: nobody publishes packages for `net48-revit2024`, so
restore is told it may fall back to `net48`. On the .NET axis it gains a Windows-flavoured entry
first - the Revit API packages publish their reference assemblies under `net8.0-windows7.0` and
`net10.0-windows7.0`, which a `revit`-platform project cannot consume directly. The Windows version
in that entry is a ceiling for resolution, high enough to also cover packages that ask for a
specific Windows SDK; it is not a claim about the project.

Between them these two properties are all that was ever needed; the translate-and-dispatch restore
existed to avoid them.

`NU1701` and `NU1702`, the notices that the fallback was used, are suppressed. They have to be
suppressed unconditionally in `Before.Microsoft.NET.Sdk.props` rather than alongside the rest of
the Revit-specific properties: NuGet records warning properties once per project, taken from the
cross-targeting outer build, which has no Revit framework of its own. Written conditionally, the
warnings escape to the solution level where nothing suppresses them.

`NU1202` is deliberately not suppressed, although the previous design had to suppress it. With the
moniker kept real, NU1202 means what it says - the package has nothing for this framework - and
hiding it would only defer the failure to run time.

### Project references

Nothing special is needed. A Revit project referencing another Revit project gets the matching
Revit framework; a Revit project referencing a project on the plain .NET axis gets the nearest
plain framework, because `AssetTargetFallback` is passed to the reference negotiation as well:

| Referencing framework | `BHS.Revit.Abstractions` resolves to | `BHS.Shared` resolves to |
|---|---|---|
| `net48-revit2024` | `.NETFramework,Version=v4.8,Profile=revit2024` | `.NETFramework,Version=v4.8` |
| `net8.0-revit2025` | `.NETCoreApp,Version=v8.0` | `.NETCoreApp,Version=v8.0` |
| `net8.0-revit2026` | `.NETCoreApp,Version=v8.0` | `.NETCoreApp,Version=v8.0` |
| `net10.0-revit2027` | `.NETCoreApp,Version=v10.0` | `.NETCoreApp,Version=v10.0` |

The dependency on the plain axis is restored once, on its own terms, and its assets file lists its
own frameworks. The old design needed `RemoveProperties` on every dispatch to get this, because a
framework passed as a global property flows down every `ProjectReference` and made the dependency
evaluate as whatever its consumer was compiling.

### Language version on Revit 2024

On .NET Framework the compiler defaults to C# 7.3, so `Nullable=enable` fails with CS8630. A
`net48-revit` moniker exists precisely to write modern code against an old framework, so the SDK
supplies `LangVersion=latest` rather than making every project repeat it. It is a default: a
project that sets `LangVersion` keeps its value. Note that `latest` only unlocks syntax - records,
`init` accessors, indices and ranges still want a polyfill package on `net48`.

### Central Package Management

Under CPM a `Version` attribute on `PackageReference` is an error (NU1008), so the implicit Revit
API references are declared as a bare `PackageReference` plus a matching `PackageVersion`. The
version floats on the Revit release (`2026.*`), and NuGet rejects a floating `PackageVersion`
with NU1011 unless `CentralPackageFloatingVersionsEnabled` is on; the SDK defaults it on in
`After.Microsoft.NET.Sdk.props`, where `Directory.Packages.props` has already been read. Setting
it in `Directory.Build.props` takes the decision back.

Do not list `Nice3point.Revit.Api.*` in `Directory.Packages.props` as well: two `PackageVersion`
items for one id is NU1506.

### Output layout

Ordinary. `bin\<Configuration>\<tfm>\` and `obj\<Configuration>\<tfm>\`, with the platform folder
when one applies, exactly as for any multi-targeting project. The Revit monikers are distinct, so
nothing needs redirecting - the `obj\RevitNNNN\` and `bin\RevitNNNN\` subtrees of the previous
design existed only because translation collapsed 2025 and 2026 onto the same inner moniker.

### Diagnostics

`dotnet build -p:RevitSdkDiagnostics=true` prints, per framework, what the SDK made of the
moniker: Revit release, framework identity, platform and asset fallback.
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
