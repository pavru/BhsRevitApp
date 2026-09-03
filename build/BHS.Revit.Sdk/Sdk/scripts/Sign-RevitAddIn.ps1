<#
.SYNOPSIS
    Signs the assembly Revit checks, with a certificate from a Windows certificate store.

.DESCRIPTION
    Called by Revit.Signing.targets. A script rather than an inline command because the inline
    version was unreadable both here and, worse, in the MSBuild error that quotes it back.

    Windows PowerShell rather than signtool.exe: Set-AuthenticodeSignature is on every Windows,
    while signtool ships with the Windows SDK at a path that moves with its version. There is no
    managed API for embedding an Authenticode signature, so a child process is the only route.

.NOTES
    A signature Windows will not validate is worse than none: Revit answers it with a dialog whose
    default is "do not load", and unlike the unsigned dialog, trusting the add-in does not silence
    it. So the result is verified here and reported, rather than left to be discovered at start-up.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Path,
    [Parameter(Mandatory)] [string] $Thumbprint,
    [string] $Store = 'Cert:\CurrentUser\My',
    [string] $HashAlgorithm = 'SHA256',
    [string] $TimestampServer = ''
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $Path)) {
    Write-Error "There is nothing to sign at '$Path'."
    exit 1
}

$certificate = Get-Item -LiteralPath (Join-Path $Store $Thumbprint) -ErrorAction SilentlyContinue

if (-not $certificate) {
    Write-Error @"
No certificate $Thumbprint in $Store.
Either create one and trust it - see "Доверие Revit к add-in" in CLAUDE.md - or leave
RevitSignWith empty to build unsigned, which is what a production build does.
"@
    exit 1
}

$arguments = @{
    FilePath      = $Path
    Certificate   = $certificate
    HashAlgorithm = $HashAlgorithm
}

# Left empty by default. A timestamp keeps a signature valid past the certificate's expiry, which
# matters for something shipped and not for a development certificate - and reaching a timestamp
# server would put the network on the critical path of every build.
if ($TimestampServer) {
    $arguments['TimestampServer'] = $TimestampServer
}

$signed = Set-AuthenticodeSignature @arguments

if ($signed.Status -ne 'Valid') {
    # Signing itself can succeed while the result does not validate - most often because the
    # certificate chain is not trusted on this machine. Revit will still ask about such a file, so
    # this is worth saying out loud, but it is not a reason to fail the build.
    Write-Host "RVTSIGN001: $([System.IO.Path]::GetFileName($Path)) signed, but the signature is $($signed.Status)."
    Write-Host "RVTSIGN001: Revit will still ask about it. Trust the certificate in Cert:\CurrentUser\Root and Cert:\CurrentUser\TrustedPublisher."
}

exit 0
