# Builds the files of a GitHub release into dist\, for install.ps1 to install from.
#
#   .\scripts\make-release.ps1 -Version 0.1.0
#
# dist\ then holds, under names that stay the same from one release to the next (so install.ps1 can
# fetch them from releases/latest/download/):
#   Palette-win-x64.zip   the app (self-contained: no .NET or Windows App SDK needed), without models
#   plan.gguf             the fine-tuned plan model (models\planner), from ml\intent
#   palette-npu.zip       the NPU versions of the plan and embedding models (ml\npu\export.py)
#   install.ps1           the installer, also inside the app zip (it uninstalls from there)
#   SHA256SUMS.txt        checked by install.ps1 before it unpacks anything
# Every other model install.ps1 fetches from where it is published (Hugging Face, llama.cpp, PyPI).
param([Parameter(Mandatory)][string]$Version)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $repo "dist"
$stage = Join-Path $dist "stage\Palette"
$models = Join-Path $repo "models"

foreach ($need in "planner\plan.gguf", "planner-npu\openvino_model.xml", "embeddinggemma-2-text-q4\model_npu.xml") {
    if (-not (Test-Path (Join-Path $models $need))) { throw "missing models\$need (see README, Build & run)" }
}

Remove-Item -Recurse -Force $dist -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $stage | Out-Null

# 1. The app, built straight into the staging folder so nothing left over from earlier builds comes along.
Push-Location $repo
try {
    cargo build --release
    if ($LASTEXITCODE -ne 0) { throw "cargo build failed" }
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
    dotnet build winui\Palette.WinUI\Palette.WinUI.csproj -c Release -p:Platform=x64 -p:PaletteNoModels=true "-p:OutDir=$stage\"
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed" }
} finally { Pop-Location }
Get-ChildItem $stage -Filter *.pdb | Remove-Item
Copy-Item (Join-Path $repo "install.ps1") $stage
Copy-Item (Join-Path $PSScriptRoot "GEMMA-NOTICE.txt") $stage
Set-Content (Join-Path $stage "version.txt") $Version -Encoding ascii

# tar.exe (in Windows since 1803) writes a zip far faster than Compress-Archive.
tar.exe -a -cf (Join-Path $dist "Palette-win-x64.zip") -C $stage *
if ($LASTEXITCODE -ne 0) { throw "zip failed" }

# 2. The models only this repo has.
Copy-Item (Join-Path $models "planner\plan.gguf") $dist
$npu = Join-Path $dist "stage\npu"
New-Item -ItemType Directory -Force (Join-Path $npu "embeddinggemma-2-text-q4") | Out-Null
Copy-Item (Join-Path $models "planner-npu") $npu -Recurse
Copy-Item (Join-Path $models "embeddinggemma-2-text-q4\model_npu.*") (Join-Path $npu "embeddinggemma-2-text-q4")
Copy-Item (Join-Path $PSScriptRoot "GEMMA-NOTICE.txt") $npu
tar.exe -a -cf (Join-Path $dist "palette-npu.zip") -C $npu *
if ($LASTEXITCODE -ne 0) { throw "zip failed" }

Copy-Item (Join-Path $repo "install.ps1") $dist
Remove-Item -Recurse -Force (Join-Path $dist "stage")

# 3. Checksums, in sha256sum's format.
$sums = Get-ChildItem $dist -File | Where-Object Name -ne "SHA256SUMS.txt" | ForEach-Object {
    "$((Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower())  $($_.Name)"
}
Set-Content (Join-Path $dist "SHA256SUMS.txt") $sums -Encoding ascii

Get-ChildItem $dist | Format-Table Name, @{ n = "MB"; e = { [math]::Round($_.Length / 1MB) } } -AutoSize
Write-Host "Release v$Version ready in $dist. To publish (GitHub CLI):"
Write-Host "  gh release create v$Version dist\* --title `"Palette $Version`" --notes-file <notes>"
