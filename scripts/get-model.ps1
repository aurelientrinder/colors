# Downloads the EmbeddingGemma 2 text model (4-bit ONNX) used by the semantic tier (~207 MB).
#
# Source:  https://huggingface.co/onnx-community/embeddinggemma-2-ONNX  (Apache-2.0)
# Target:  <repo>\models\embeddinggemma-2-text-q4   (copied beside PaletteWinUI.exe by the WinUI build)
#
# Only the text graph is fetched; the vision and audio encoders are not needed for text search.
# Safe to re-run: files that are already complete are skipped.
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $repo "models\embeddinggemma-2-text-q4"
New-Item -ItemType Directory -Force $dest | Out-Null

$base = "https://huggingface.co/onnx-community/embeddinggemma-2-ONNX/resolve/main"
$files = @(
    @{ Url = "$base/onnx/model_q4.onnx";      Name = "model_q4.onnx";      MinBytes = 100KB },
    @{ Url = "$base/onnx/model_q4.onnx_data"; Name = "model_q4.onnx_data"; MinBytes = 150MB },
    @{ Url = "$base/tokenizer.json";          Name = "tokenizer.json";     MinBytes = 20MB },
    @{ Url = "$base/config.json";             Name = "config.json";        MinBytes = 100 }
)

foreach ($f in $files) {
    $out = Join-Path $dest $f.Name
    if ((Test-Path $out) -and (Get-Item $out).Length -ge $f.MinBytes) {
        Write-Host "ok       $($f.Name)"
        continue
    }
    Write-Host "download $($f.Name)"
    curl.exe -L --fail --retry 3 -o $out $f.Url
    if ($LASTEXITCODE -ne 0) { throw "download failed: $($f.Url)" }
}
Write-Host "Model ready in $dest"
