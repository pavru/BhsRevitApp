<#
.SYNOPSIS
    Verifies a recorded in-Revit sweep. DOES NOT RUN ONE.

.DESCRIPTION
    Read this first, because the distinction is the whole point of the file.

    Revit cannot run on hosted CI: it needs an interactive session, a licence and about a gigabyte
    of Autodesk on disk. No check in this repository has ever started a Revit, and none ever will
    from a runner. What the sweep proves - that the add-in loads, that the channel works inside the
    process, that the ribbon builds - is proved on a developer machine, by a person, using
    BHS.Revit.Probe.Runner.

    This script checks the RECORD of that run. It is a real check with real teeth: it can tell that
    the record is missing, stale, incomplete, failing, or quietly smaller than it used to be. It
    cannot tell whether the sweep was honest, and it is not evidence that Revit works - only that
    somebody's evidence exists and is current.

    Five things, each answering a way the record could be worthless:

      1. it exists, parses, and its schema is one this script understands;
      2. it was recorded on a clean tree - a sweep over uncommitted edits tested something that
         exists on one machine and nowhere else;
      3. its commit is an ancestor of HEAD, and nothing Revit-side changed after it. This is the one
         that matters: without it, last week's report passes forever;
      4. every supported release is present, and nothing failed;
      5. no check present in the base report has disappeared. A check that stops running prints
         nothing and fails nothing - which is how RefCheck went months unimported here.

.PARAMETER Path
    The report to check. Defaults to evidence/sweep-report.json.

.PARAMETER Base
    A git revision holding the previous report, for the disappearing-check comparison. Skipped when
    the base carries no report yet.

.OUTPUTS
    Exit code is the number of problems, so zero is success.
#>
[CmdletBinding()]
param(
    [string] $Path,
    [string] $Base
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Resolve-Path (Join-Path (Join-Path $PSScriptRoot '..') '..')
# Two-argument Join-Path throughout: the three-argument form arrived in PowerShell 6, and this
# script is meant to be runnable by hand before pushing - where `powershell` is still 5.1 and fails
# with "A positional parameter cannot be found", naming nothing about the report. Measured.
if (-not $Path) { $Path = Join-Path $root 'evidence/sweep-report.json' }

# Everything whose change could alter what a sweep would find. Documentation and CI wiring are not
# here on purpose: requiring twenty minutes of Revit to fix a typo is how a rule gets switched off.
# source/WinSide is here because BHS.Revit.Launch owns launching Revit, waiting for the
# registration, asking it to close and killing it - it decides the outcome of checks like "the
# add-in registers over the well-known pipe" as directly as the add-in does. Left out, an edit to
# it would leave a stale sweep passing unchallenged.
$revitSide = @('source/Revit', 'source/Shared', 'source/WinSide', 'build/BHS.Revit.Sdk')

# Read from the SDK rather than repeated here. CLAUDE.md promises that adding a Revit release is
# one line in Revit.Identity.targets; a second list in this file would mean the new release is
# never required to appear in a sweep - the very "check that stops running" this script exists to
# catch, committed by the script itself.
$identity = Join-Path $root 'build/BHS.Revit.Sdk/Sdk/targets/Revit.Identity.targets'
$supported = @()

if (Test-Path $identity) {
    $declared = Select-String -Path $identity -Pattern 'RevitSupportedReleases[^>]*>([0-9][^<]*)<' | Select-Object -First 1

    if ($declared) {
        $supported = @($declared.Matches[0].Groups[1].Value -split ';' |
            ForEach-Object { ($_ -split '=')[0].Trim() } |
            Where-Object { $_ -match '^[0-9]{4}$' } |
            ForEach-Object { [int] $_ })
    }
}
$knownSchema = 1
$problems = 0

if ($supported.Count -eq 0) {
    # A completeness check with nothing to be complete against passes on anything. Better to say so
    # than to go quietly green - this script's whole subject is checks that stop checking.
    Write-Host "check-sweep-report: could not read the supported releases from $identity." -ForegroundColor Red
    exit 1
}

<#
  Reads a field that may not be there at all.

  StrictMode turns a missing property into a terminating error, so `if (-not $x.Field)` - written to
  produce a careful sentence when the field is absent - instead kills the script with "the property
  cannot be found", and the exit code stops being the problem count. Measured on a real report with
  one field removed. The writer no longer omits nulls, but a record produced by an older build, or
  by anything else, still can.
#>
function Field($object, [string] $name) {
    if ($null -eq $object) { return $null }
    $property = $object.PSObject.Properties[$name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Summarise {
    $mode = if ($report.WithModel) { 'with a model' } else { 'without a model' }
    $mode += if ($report.ShowTab) { ', ribbon exercised' } else { ', ribbon not exercised' }
    # Formatted rather than printed as it comes: ConvertFrom-Json turns the ISO string into a
    # DateTime, and Write-Host then renders it in the runner's culture - "09/06/2026", which is
    # either the sixth of September or the ninth of June depending on where the reader is from.
    # Parsed as an offset and converted, not cast to [datetime]: Windows PowerShell 5.1 leaves the
    # ISO string alone where pwsh turns it into a DateTime, and the two then print times ten hours
    # apart on this machine. A timestamp that means something different depending on who reads it is
    # worse than none, because it looks like a fact.
    # Both shapes, because the two shells disagree about what ConvertFrom-Json produces: pwsh hands
    # back a DateTime already, Windows PowerShell 5.1 leaves the ISO string. Round-tripping the
    # DateTime through Parse read 06.09 back as the sixth of June; parsing the string with the
    # current culture would do the same to the other one. So: convert what is there, and parse only
    # what is still text, invariantly.
    $raw = Field $report 'RecordedUtc'
    $utc = if ($raw -is [datetime]) { $raw.ToUniversalTime() }
           else { [datetimeoffset]::Parse([string] $raw, [cultureinfo]::InvariantCulture).UtcDateTime }
    $when = $utc.ToString('yyyy-MM-dd HH:mm:ss', [cultureinfo]::InvariantCulture) + 'Z'

    Write-Host ("check-sweep-report: {0} checks across {1} release(s), recorded {2} at {3} ({4}). Verified, not reproduced." -f `
        $report.Performed, $report.Releases.Count, $when, $commit.Substring(0, 8), $mode) -ForegroundColor Green
}

function Fail([string] $message) {
    Write-Host "check-sweep-report: $message" -ForegroundColor Red
    $script:problems++
}

Write-Host "check-sweep-report: verifying a RECORDED sweep. CI does not run Revit - see the header of this script."

if (-not (Test-Path $Path)) {
    Fail "there is no report at $Path. Run: dotnet run --project source/Revit/BHS.Revit.Probe.Runner -- --report evidence/sweep-report.json"
    exit $problems
}

try { $report = Get-Content $Path -Raw | ConvertFrom-Json }
catch { Fail "the report at $Path is not valid JSON: $($_.Exception.Message)"; exit $problems }

# ---- 1. schema ---------------------------------------------------------------------------------

$schema = Field $report 'Schema'

if ($schema -ne $knownSchema) {
    Fail "the report says schema $schema, and this script understands $knownSchema."
    exit $problems
}

# ---- 2. recorded against something that exists -------------------------------------------------

if (-not (Field $report 'CommitClean')) {
    Fail 'the sweep ran over a dirty working tree, so it tested code that exists on one machine only. Commit first, then sweep, then commit the report.'
}

$commit = Field $report 'Commit'

if (-not $commit) {
    Fail 'the report does not say which commit it ran against, which makes it a record of nothing.'
    exit $problems
}

# ---- 3. still current --------------------------------------------------------------------------

& git -C $root cat-file -e "$commit^{commit}" 2>$null
if ($LASTEXITCODE -ne 0) {
    Fail "the report names commit $commit, which is not in this repository."
    exit $problems
}

& git -C $root merge-base --is-ancestor $commit HEAD
if ($LASTEXITCODE -ne 0) {
    Fail "the report's commit $commit is not an ancestor of HEAD - it describes a different line of work."
}
else {
    $changed = & git -C $root diff --name-only "$commit..HEAD" -- $revitSide

    if ($changed) {
        Fail @"
Revit-side code changed after the sweep was recorded, so the record no longer describes this branch.

Changed since ${commit}:
$($changed | ForEach-Object { "  $_" } | Out-String)
Sweep again and commit the new report:
  dotnet run --project source/Revit/BHS.Revit.Probe.Runner -- --deploy --report evidence/sweep-report.json
"@
    }
}

# ---- 4. complete, and green --------------------------------------------------------------------

$present = @($report.Releases | ForEach-Object { $_.Release })
$missing = $supported | Where-Object { $_ -notin $present }

if ($missing) {
    Fail "the sweep covers $($present -join ', '), and this repository supports $($supported -join ', '). Missing: $($missing -join ', ')."
}

if ($report.Failed -ne 0) {
    Fail "the sweep recorded $($report.Failed) failed check(s)."
}

foreach ($release in $report.Releases) {
    foreach ($check in $release.Checks) {
        if (-not (Field $check 'Ok')) { Fail "Revit $($release.Release): $($check.Name)" }
    }

    if (-not (Field $release 'FileVersion')) {
        Fail "Revit $($release.Release) has no deployed build recorded, so the checks do not say what they checked."
    }
}

# ---- 5. nothing quietly gone -------------------------------------------------------------------

if ($Base) {
    $previous = & git -C $root show "${Base}:evidence/sweep-report.json" 2>$null

    if ($LASTEXITCODE -ne 0 -or -not $previous) {
        Write-Host "check-sweep-report: $Base carries no report yet, so there is nothing to compare against." -ForegroundColor Yellow
    }
    else {
        $before = $previous | ConvertFrom-Json

        # Only against a report of the same shape. A plain sweep is 212 checks and one with a model
        # and the ribbon pressed is 288, so comparing across modes would report seventy checks as
        # "disappeared" the first time somebody recorded the cheaper one - and a check that cries
        # wolf is worse than no check, because it is the one people learn to skip.
        if ($before.WithModel -ne $report.WithModel -or $before.ShowTab -ne $report.ShowTab) {
            Write-Host "check-sweep-report: the base report was recorded in a different mode, so the two check lists are not comparable." -ForegroundColor Yellow
            if ($problems -eq 0) { Summarise } 
            exit $problems
        }

        $wasThere = @{}

        foreach ($release in $before.Releases) {
            foreach ($check in $release.Checks) { $wasThere["$($release.Release)|$($check.Name)"] = $true }
        }

        $isThere = @{}
        foreach ($release in $report.Releases) {
            foreach ($check in $release.Checks) { $isThere["$($release.Release)|$($check.Name)"] = $true }
        }

        $gone = $wasThere.Keys | Where-Object { -not $isThere.ContainsKey($_) } | Sort-Object

        # Named one by one, because the count alone is what the probe's own floor already catches -
        # and a floor only notices when several vanish at once.
        foreach ($key in $gone) {
            $parts = $key -split '\|', 2
            Fail "a check that used to run no longer does - Revit $($parts[0]): $($parts[1]). If it was removed on purpose, say so in the commit message; the base report is the baseline."
        }
    }
}

if ($problems -eq 0) { Summarise }

exit $problems
