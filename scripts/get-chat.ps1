# Sets up asking about the window in front ("summarise this", "what does this error mean"):
# llama.cpp's server, the GPU (Vulkan) build, in models\chat.
#
# Server:  https://github.com/ggml-org/llama.cpp/releases (MIT), Vulkan build, about 32 MB
# Model:   any chat GGUF of a few billion parameters. Palette looks, in this order, at
#          PALETTE_CHAT_MODEL, models\chat\*.gguf, and what LM Studio has downloaded
#          (~\.lmstudio\models). Qwen3.5 4B is preferred, then Gemma 3 4B. A model of 4B in Q4 is
#          about 2.5 GB and answers in a second or two on an Arc GPU.
# Target:  <repo>\models\chat
#
# Without it nothing changes: the row that offers an answer simply never appears.
# Safe to re-run.
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $repo "models\chat"
$tag = "b11609"
New-Item -ItemType Directory -Force $dest | Out-Null

if (-not (Test-Path (Join-Path $dest "llama-server.exe"))) {
    $zip = Join-Path $env:TEMP "llama-$tag-vulkan.zip"
    $tmp = Join-Path $env:TEMP "llama-$tag-vulkan"
    Write-Host "download llama.cpp $tag (Vulkan)"
    curl.exe -L --fail --retry 3 -o $zip "https://github.com/ggml-org/llama.cpp/releases/download/$tag/llama-$tag-bin-win-vulkan-x64.zip"
    if ($LASTEXITCODE -ne 0) { throw "download failed" }
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
    Expand-Archive $zip $tmp
    Copy-Item (Join-Path $tmp "llama-server.exe") $dest
    Copy-Item (Join-Path $tmp "*.dll") $dest
    Remove-Item -Recurse -Force $tmp, $zip
} else {
    Write-Host "ok       llama-server.exe"
}

$models = @(Get-ChildItem $dest -Filter *.gguf -ErrorAction SilentlyContinue) +
    @(Get-ChildItem (Join-Path $env:USERPROFILE ".lmstudio\models") -Recurse -Filter *.gguf -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -notmatch "mmproj" -and $_.Length -gt 1.5GB -and $_.Length -lt 12GB })
if ($models.Count -gt 0) {
    Write-Host "model    $($models[0].FullName)"
} else {
    Write-Host "No chat model found. Put a GGUF in $dest (for example Qwen3.5-4B-Q4_K_S.gguf), or download one in LM Studio."
}
