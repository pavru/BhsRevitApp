<#
.SYNOPSIS
    Applies one red run's breakages to the working tree, or takes them back off.

.DESCRIPTION
    A red run is a deliberate, minimal breakage of production code, made so that a named
    in-Revit case goes red on a named assertion. A green case only proves the code works
    today; that the case would CATCH a regression is proven by nothing until it has been
    seen to fail on purpose.

    The breakages are never committed in applied form. This script edits the working tree,
    the run is executed, and -Revert puts it back with git restore. That is why it refuses
    to start on a dirty tree: it would otherwise have no way back.

    Breakages are matched by ANCHOR TEXT and never by line number - one applied edit shifts
    every line below it, and a red run that silently patched the wrong line would look like
    evidence. Each anchor must occur exactly once in its file or nothing is applied at all.

.PARAMETER Run
    Which run to apply, 1..8. Runs 5-8 are the second round. See readme.md for what each one proves.

.PARAMETER Revert
    Take the breakages back off with git restore, instead of applying them.

.PARAMETER List
    Print what a run would do and change nothing.

.EXAMPLE
    ./build/red-runs/Apply-RedRun.ps1 -Run 1 -List
    ./build/red-runs/Apply-RedRun.ps1 -Run 1
    dotnet build BhsRevitApp.slnx -c Release
    # ... run the sweep on ONE release, without --report ...
    ./build/red-runs/Apply-RedRun.ps1 -Run 1 -Revert
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateRange(1, 8)][int] $Run,
    [switch] $Revert,
    [switch] $List
)

$ErrorActionPreference = 'Stop'

$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$setPath = Join-Path $PSScriptRoot 'breakages.json'

if (-not (Test-Path $setPath)) {
    throw "red-run: $setPath is missing, so there is nothing to apply."
}

$all = Get-Content -Raw -LiteralPath $setPath | ConvertFrom-Json
$wanted = @($all | Where-Object { $_.run -eq $Run })

if ($wanted.Count -eq 0) {
    throw "red-run: the set declares no breakage for run $Run."
}

$files = @($wanted | ForEach-Object { $_.file } | Sort-Object -Unique)

if ($List) {
    Write-Host "red-run ${Run}: $($wanted.Count) breakage(s) over $($files.Count) file(s)" -ForegroundColor Cyan
    foreach ($b in $wanted) {
        Write-Host ''
        Write-Host "  $($b.file)" -ForegroundColor Yellow
        Write-Host "    case    : $($b.case)"
        Write-Host "    reddens : $($b.reddens)"
        Write-Host "    anchor  : $(($b.anchor -replace '\s+', ' ').Trim())"
    }
    return
}

Push-Location $root
try {
    if ($Revert) {
        # git restore and not a reverse replacement: the reverse of an edit that did not
        # apply cleanly is another edit that does not apply cleanly, and the point of the
        # revert is to be certain, not clever.
        git restore -- $files
        if ($LASTEXITCODE -ne 0) { throw "red-run: git restore failed for $($files -join ', ')." }

        $left = git status --porcelain -- $files
        if ($left) { throw "red-run: $($files -join ', ') still differ after restore:`n$left" }

        Write-Host "red-run ${Run}: reverted $($files.Count) file(s)." -ForegroundColor Green
        return
    }

    # A dirty tree has no way back, and a red run started from one proves nothing about
    # the committed code anyway.
    $dirty = git status --porcelain -- $files
    if ($dirty) {
        throw "red-run: these files already differ from HEAD, so there would be no way back:`n$dirty"
    }

    # Read and check every file BEFORE writing any of them: a set half applied is the one
    # state nothing downstream can reason about.
    $plan = @()
    foreach ($b in $wanted) {
        $path = Join-Path $root $b.file
        if (-not (Test-Path $path)) { throw "red-run: $($b.file) is not in the tree." }

        $bytes = [System.IO.File]::ReadAllBytes($path)
        $bom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
        $text = [System.Text.Encoding]::UTF8.GetString($bytes, $(if ($bom) { 3 } else { 0 }), $bytes.Length - $(if ($bom) { 3 } else { 0 }))

        # The set stores anchors with LF; the tree may hold CRLF. Match in the file's own
        # spelling rather than rewriting every line ending of a file we mean to touch once.
        $eol = if ($text.Contains("`r`n")) { "`r`n" } else { "`n" }
        $anchor = $b.anchor -replace "`r`n", "`n" -replace "`n", $eol
        $replacement = $b.replacement -replace "`r`n", "`n" -replace "`n", $eol

        $hits = ([regex]::Matches($text, [regex]::Escape($anchor))).Count
        if ($hits -ne 1) {
            throw "red-run: the anchor for '$($b.case)' occurs $hits time(s) in $($b.file), not once. The set is stale against this tree - do not guess, re-derive it."
        }

        $plan += $b.file
    }

    Write-Host "red-run ${Run}: $($plan.Count) anchor(s) found, one each. Applying." -ForegroundColor Cyan

    # Applied one after another, re-reading each time: several breakages share CablingApply.cs,
    # and each has to be found in the text the previous edit left behind, not in the pristine one.
    foreach ($b in $wanted) {
        $path = Join-Path $root $b.file
        $bytes = [System.IO.File]::ReadAllBytes($path)
        $bom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
        $skip = $(if ($bom) { 3 } else { 0 })
        $text = [System.Text.Encoding]::UTF8.GetString($bytes, $skip, $bytes.Length - $skip)

        $eol = if ($text.Contains("`r`n")) { "`r`n" } else { "`n" }
        $anchor = $b.anchor -replace "`r`n", "`n" -replace "`n", $eol
        $replacement = $b.replacement -replace "`r`n", "`n" -replace "`n", $eol

        $hits = ([regex]::Matches($text, [regex]::Escape($anchor))).Count
        if ($hits -ne 1) {
            git restore -- $files
            throw "red-run: the anchor for '$($b.case)' occurs $hits time(s) once the earlier edits of this run are in. Reverted; the set needs re-deriving."
        }

        $text = $text.Replace($anchor, $replacement)

        $encoding = New-Object System.Text.UTF8Encoding($bom)
        [System.IO.File]::WriteAllBytes($path, $encoding.GetPreamble() + $encoding.GetBytes($text))

        Write-Host "  broke: $($b.file)" -ForegroundColor Yellow
        Write-Host "         must redden - $($b.reddens)"
    }

    Write-Host ''
    Write-Host "red-run ${Run}: $($wanted.Count) breakage(s) applied. Build, deploy and run ONE release without --report." -ForegroundColor Cyan
    Write-Host "Put it back with: ./build/red-runs/Apply-RedRun.ps1 -Run $Run -Revert" -ForegroundColor Cyan
}
finally {
    Pop-Location
}
