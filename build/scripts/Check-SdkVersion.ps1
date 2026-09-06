<#
.SYNOPSIS
    Checks that the BHS.Revit.Sdk version is consistent, and that it moved when the SDK did.

.DESCRIPTION
    Two failures live here, and both are ones this repository has already paid for.

    The first is a mismatch: the SDK version appears in its own project file and again in the
    header of every Revit-side project that names it. Miss one and the build says
    "Could not resolve SDK", which points at the header rather than at the edit that caused it.
    This half needs no git and runs anywhere.

    The second is the expensive one, and it has no symptom at all. The SDK package carries an
    MSBuild task assembly, and any process that has evaluated a Revit project holds
    BHS.Revit.Sdk.dll open - so rebuilding at the SAME version leaves the extracted copy in the
    NuGet cache untouched, and the build keeps running code that no longer exists in the tree.
    Two SDK versions were burnt that way before this check existed. Nothing breaks, nothing warns:
    exactly the shape the rules describe as held only by being written down somewhere visible.
    Written down is not enough, so it is checked instead.

.PARAMETER Since
    A git revision to compare against - the base of a pull request, usually origin/master. Without
    it only the consistency half runs, which is what makes the script useful as a local pre-flight.

.OUTPUTS
    Exit code is the number of violations, so zero is success.
#>
[CmdletBinding()]
param(
    [string] $Since
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Resolve-Path (Join-Path $PSScriptRoot '..' '..')
$sdkProject = Join-Path $root 'build/BHS.Revit.Sdk/BHS.Revit.Sdk.csproj'

# Paths whose content ends up deciding how a Revit project builds. readme.md and images/ ride
# along in the package but change nothing, and a version bump costs an edit in five project
# headers plus a reload in the IDE - too much to spend on a typo in prose.
$watched = @(
    'build/BHS.Revit.Sdk/Sdk',
    'build/BHS.Revit.Sdk/tasks',
    'build/BHS.Revit.Sdk/BHS.Revit.Sdk.csproj'
)

$violations = 0

function Fail([string] $message) {
    Write-Host "check-sdk-version: $message" -ForegroundColor Red
    $script:violations++
}

function Get-DeclaredVersion([string] $text, [string] $what) {
    if ($text -match '<Version>\s*([^<\s]+)\s*</Version>') { return $Matches[1] }
    Fail "$what does not declare a <Version>."
    return $null
}

if (-not (Test-Path $sdkProject)) {
    Fail "the SDK project is not at $sdkProject."
    exit $violations
}

$declared = Get-DeclaredVersion (Get-Content $sdkProject -Raw) 'the SDK project'
if (-not $declared) { exit $violations }

# ---- half one: every header agrees with the SDK's own version -------------------------------

$consumers = Get-ChildItem -Path (Join-Path $root 'source') -Recurse -Filter *.csproj |
    Where-Object { $_.FullName -notmatch '[\\/](obj|bin)[\\/]' }

$named = 0
foreach ($consumer in $consumers) {
    $text = Get-Content $consumer.FullName -Raw
    if ($text -notmatch 'Sdk\s*=\s*"BHS\.Revit\.Sdk/([^"]+)"') { continue }

    $named++
    $used = $Matches[1]
    if ($used -ne $declared) {
        $relative = $consumer.FullName.Substring($root.Path.Length + 1)
        Fail "$relative names BHS.Revit.Sdk/$used, but the SDK is $declared."
    }
}

if ($named -eq 0) {
    # A check that finds nothing to check is not a passing check. If the header syntax ever
    # changes, this is the line that says so instead of going quietly green forever.
    Fail 'no project names BHS.Revit.Sdk in its header - the pattern this script matches must have changed.'
}

# ---- half two: the version moved when the SDK did --------------------------------------------

if ($Since) {
    & git -C $root rev-parse --verify --quiet "$Since^{commit}" > $null
    if ($LASTEXITCODE -ne 0) {
        Fail "the base revision '$Since' does not exist here. Fetch it: a base that cannot be read is not a check that passed."
        exit $violations
    }

    $changed = & git -C $root diff --name-only "$Since...HEAD" -- $watched
    if ($LASTEXITCODE -ne 0) {
        Fail "git diff against '$Since' failed."
        exit $violations
    }

    if ($changed) {
        $before = & git -C $root show "${Since}:build/BHS.Revit.Sdk/BHS.Revit.Sdk.csproj" 2>$null
        if ($LASTEXITCODE -ne 0) {
            Write-Host "check-sdk-version: the SDK project is new since $Since; nothing to compare." -ForegroundColor Yellow
        }
        else {
            $baseVersion = Get-DeclaredVersion ($before -join "`n") "the SDK project at $Since"
            if ($baseVersion -and $baseVersion -eq $declared) {
                Fail @"
the SDK changed but its version did not - still $declared.

Changed:
$($changed | ForEach-Object { "  $_" } | Out-String)
Raise <Version> in build\BHS.Revit.Sdk\BHS.Revit.Sdk.csproj and in the header of every project
that names it. Building a new SDK at an old version leaves the extracted copy in the NuGet cache
in place, so the build silently keeps the code you just replaced.
"@
            }
        }
    }
}

if ($violations -eq 0) {
    Write-Host "check-sdk-version: BHS.Revit.Sdk $declared, agreed by $named project header(s)." -ForegroundColor Green
}

exit $violations
