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

$root = Resolve-Path (Join-Path $PSScriptRoot '..' '..')
if (-not $Path) { $Path = Join-Path $root 'evidence/sweep-report.json' }

# Everything whose change could alter what a sweep would find. Documentation and CI wiring are not
# here on purpose: requiring twenty minutes of Revit to fix a typo is how a rule gets switched off.
$revitSide = @('source/Revit', 'source/Shared', 'build/BHS.Revit.Sdk')

$supported = @(2024, 2025, 2026, 2027)
$knownSchema = 1
$problems = 0

function Summarise {
    $mode = if ($report.WithModel) { 'with a model' } else { 'without a model' }
    $mode += if ($report.ShowTab) { ', ribbon exercised' } else { ', ribbon not exercised' }
    Write-Host ("check-sweep-report: {0} checks across {1} release(s), recorded {2} at {3} ({4}). Verified, not reproduced." -f `
        $report.Performed, $report.Releases.Count, $report.RecordedUtc, $report.Commit.Substring(0, 8), $mode) -ForegroundColor Green
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

if ($report.Schema -ne $knownSchema) {
    Fail "the report says schema $($report.Schema), and this script understands $knownSchema."
    exit $problems
}

# ---- 2. recorded against something that exists -------------------------------------------------

if (-not $report.CommitClean) {
    Fail 'the sweep ran over a dirty working tree, so it tested code that exists on one machine only. Commit first, then sweep, then commit the report.'
}

if (-not $report.Commit) {
    Fail 'the report does not say which commit it ran against, which makes it a record of nothing.'
    exit $problems
}

# ---- 3. still current --------------------------------------------------------------------------

& git -C $root cat-file -e "$($report.Commit)^{commit}" 2>$null
if ($LASTEXITCODE -ne 0) {
    Fail "the report names commit $($report.Commit), which is not in this repository."
    exit $problems
}

& git -C $root merge-base --is-ancestor $report.Commit HEAD
if ($LASTEXITCODE -ne 0) {
    Fail "the report's commit $($report.Commit) is not an ancestor of HEAD - it describes a different line of work."
}
else {
    $changed = & git -C $root diff --name-only "$($report.Commit)..HEAD" -- $revitSide

    if ($changed) {
        Fail @"
Revit-side code changed after the sweep was recorded, so the record no longer describes this branch.

Changed since $($report.Commit):
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
        if (-not $check.Ok) { Fail "Revit $($release.Release): $($check.Name)" }
    }

    if (-not $release.FileVersion) {
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
