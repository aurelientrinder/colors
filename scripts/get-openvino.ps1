# Sets up the OpenVINO runtime that runs the models on the NPU (Intel Core Ultra "AI Boost").
#
# Source:  OpenVINO 2026.4.1 (Apache-2.0): the DLLs of its Python wheel on PyPI, or of
#          ml\intent\.venv when it is there. Only the NPU plugin is kept, and the model is
#          compiled by the NPU driver, so its 100 MB compiler is left out: about 25 MB.
# Target:  <repo>\models\openvino   (copied beside PaletteWinUI.exe by the WinUI build)
#
# Without it, or on a PC without an NPU, the models run as before (llama.cpp on the CPU for
# plans, DirectML for search). Safe to re-run.
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $repo "models\openvino"
$version = "2026.4.1"
$dlls = @(
    "openvino.dll", "openvino_c.dll", "openvino_intel_npu_plugin.dll",
    "openvino_ir_frontend.dll", "tbb12.dll", "tbbmalloc.dll"
)
New-Item -ItemType Directory -Force $dest | Out-Null

$libs = Join-Path $repo "ml\intent\.venv\Lib\site-packages\openvino\libs"
$tmp = $null
$have = (Test-Path (Join-Path $libs "openvino_c.dll")) -and
        ((Get-Item (Join-Path $libs "openvino.dll")).VersionInfo.ProductVersion -like "$version*")
if (-not $have) {
    $meta = Invoke-RestMethod "https://pypi.org/pypi/openvino/$version/json"
    $wheel = $meta.urls | Where-Object { $_.filename -like "*win_amd64.whl" } | Select-Object -First 1
    if (-not $wheel) { throw "no Windows wheel for openvino $version" }
    $tmp = Join-Path $env:TEMP "openvino-$version"
    $zip = "$tmp.zip"
    Write-Host "download $($wheel.filename)"
    curl.exe -L --fail --retry 3 -o $zip $wheel.url
    if ($LASTEXITCODE -ne 0) { throw "download failed" }
    if ((Get-FileHash $zip -Algorithm SHA256).Hash -ne $wheel.digests.sha256.ToUpper()) { throw "checksum mismatch" }
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
    Expand-Archive $zip $tmp
    Remove-Item $zip
    $libs = Join-Path $tmp "openvino\libs"
}
foreach ($d in $dlls) {
    Copy-Item (Join-Path $libs $d) $dest -Force
}
if ($tmp) { Remove-Item -Recurse -Force $tmp }
Write-Host "OpenVINO $version ready in $dest"
