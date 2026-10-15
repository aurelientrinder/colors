<#
Installs Palette with UI Access, so the Windows key opens it over elevated windows (Task Manager,
an admin terminal) like everywhere else, while it stays an ordinary unelevated app.

Windows runs a UI Access exe only when it is signed by a certificate this PC trusts and it sits in
a secure folder. So this script, run as administrator:
  1. copies the Install build (dotnet build -c Install, run first without admin) to
     C:\Program Files\Palette;
  2. makes a new self-signed code-signing certificate, trusts it on this PC (Trusted Root and
     Trusted Publishers, local machine), signs PaletteWinUI.exe with it, then DELETES its private
     key: nothing can be signed with it afterwards. Earlier Palette certificates are removed first;
  3. checks the signature, and removes the PaletteKeys task of an elevated helper an earlier
     version used for this, if it is still there.

Run again after each rebuild. Uninstall: scripts\install-uiaccess.ps1 -Uninstall
#>
#Requires -RunAsAdministrator
param([switch]$Uninstall)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$source = Join-Path $repo 'winui\Palette.WinUI\bin\x64\Install\net10.0-windows10.0.19041.0\win-x64'
$dest = Join-Path $env:ProgramFiles 'Palette'
$subject = 'CN=Palette local UI Access signing'

function Stop-Installed {
    Get-Process PaletteWinUI, palette-semantic, palette-plan -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($dest, [StringComparison]::OrdinalIgnoreCase) } |
        Stop-Process -Force
    Start-Sleep -Milliseconds 500
}

function Remove-PrivateKey($cert) {
    # Windows PowerShell 5.1 has no Remove-Item -DeleteKey: delete the (CNG) key itself.
    $rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($cert)
    if ($rsa -is [System.Security.Cryptography.RSACng]) { $rsa.Key.Delete() }
}

function Remove-Certificates {
    foreach ($store in 'Root', 'TrustedPublisher', 'My') {
        foreach ($c in Get-ChildItem "Cert:\LocalMachine\$store" | Where-Object Subject -eq $subject) {
            if ($c.HasPrivateKey) { Remove-PrivateKey $c }
            Remove-Item $c.PSPath
        }
    }
}

if ($Uninstall) {
    Stop-Installed
    if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
    Remove-Certificates
    Write-Host 'Removed C:\Program Files\Palette and the Palette signing certificate.'
    return
}

if (-not (Test-Path (Join-Path $source 'PaletteWinUI.exe'))) {
    throw "No Install build at $source. Run first (not as admin): dotnet build -c Install  in winui\Palette.WinUI"
}

# 1. Copy.
Stop-Installed
robocopy $source $dest /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Copy to $dest failed (robocopy $LASTEXITCODE)" }
$exe = Join-Path $dest 'PaletteWinUI.exe'

# 2. Certificate: trusted, used once, private key deleted.
Remove-Certificates
$cert = New-SelfSignedCertificate -Subject $subject -Type CodeSigningCert -CertStoreLocation Cert:\LocalMachine\My `
    -Provider 'Microsoft Software Key Storage Provider' -KeyExportPolicy NonExportable -NotAfter (Get-Date).AddYears(20)
$cer = Join-Path $env:TEMP 'palette-signing.cer'
Export-Certificate -Cert $cert -FilePath $cer | Out-Null
Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\TrustedPublisher | Out-Null
Remove-Item $cer
Set-AuthenticodeSignature -FilePath $exe -Certificate $cert -HashAlgorithm SHA256 | Out-Null
Remove-PrivateKey $cert
Remove-Item "Cert:\LocalMachine\My\$($cert.Thumbprint)"

# 3. Check, then retire the helper.
$sig = Get-AuthenticodeSignature $exe
if ($sig.Status -ne 'Valid') { throw "Signature check failed: $($sig.Status) $($sig.StatusMessage)" }
if (Get-ScheduledTask -TaskName PaletteKeys -ErrorAction SilentlyContinue) {
    Stop-ScheduledTask -TaskName PaletteKeys -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName PaletteKeys -Confirm:$false
}
Get-Process palette-keys -ErrorAction SilentlyContinue | Stop-Process -Force

Write-Host "Installed to $dest, signed ($($sig.SignerCertificate.Thumbprint)). Start it from there."
