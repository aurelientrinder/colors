# Builds the Rust engine + admin helper, then the WinUI 3 shell.
# Outputs:
#   target\release\palette_core.dll                            engine (C ABI)
#   target\release\palette-mft.exe                             admin helper
#   winui\Palette.WinUI\bin\x64\Release\...\PaletteWinUI.exe   the app (engine + helper copied beside it)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

cargo build --release
if ($LASTEXITCODE -ne 0) { throw "cargo build failed" }

$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
dotnet build winui\Palette.WinUI\Palette.WinUI.csproj -c Release -p:Platform=x64
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed" }
