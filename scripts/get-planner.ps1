# Sets up the plan model: llama.cpp's server (CPU build) and the fine-tuned Gemma 3 270M.
#
# Server:  https://github.com/ggml-org/llama.cpp/releases (MIT), CPU build, about 18 MB
# Model:   ml\intent\runs\gguf\plan-Q8_0.gguf, made by ml\intent (train.py, then the GGUF
#          conversion in its README); about 280 MB
# Target:  <repo>\models\planner   (copied beside PaletteWinUI.exe by the WinUI build)
#
# Without it the launcher works as before: requests are read by the rules only.
# Safe to re-run.
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $repo "models\planner"
$tag = "b11609"
New-Item -ItemType Directory -Force $dest | Out-Null

if (-not (Test-Path (Join-Path $dest "llama-server.exe"))) {
    $zip = Join-Path $env:TEMP "llama-$tag-cpu.zip"
    $tmp = Join-Path $env:TEMP "llama-$tag-cpu"
    Write-Host "download llama.cpp $tag (CPU)"
    curl.exe -L --fail --retry 3 -o $zip "https://github.com/ggml-org/llama.cpp/releases/download/$tag/llama-$tag-bin-win-cpu-x64.zip"
    if ($LASTEXITCODE -ne 0) { throw "download failed" }
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
    Expand-Archive $zip $tmp
    Copy-Item (Join-Path $tmp "llama-server.exe") $dest
    Copy-Item (Join-Path $tmp "*.dll") $dest
    Remove-Item -Recurse -Force $tmp, $zip
} else {
    Write-Host "ok       llama-server.exe"
}

$model = Join-Path $repo "ml\intent\runs\gguf\plan-Q8_0.gguf"
if (-not (Test-Path $model)) { throw "no trained model at ${model}: see ml\intent\README.md" }
Copy-Item $model (Join-Path $dest "plan.gguf") -Force
Write-Host "Plan model ready in $dest"

