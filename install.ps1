# Installs Palette for the current user, no admin needed. In PowerShell:
#
#   irm https://raw.githubusercontent.com/aurelientrinder/colors/main/install.ps1 | iex
#
# It puts the app in %LOCALAPPDATA%\Programs\Palette, downloads the models it uses, adds Palette to the
# Start menu, to sign-in (the same "Palette" logon task as its own "Run at startup") and to Apps &
# features, then starts it. Run it again to update: models already there are kept.
#
# The models, each optional (Palette works without them, with less):
#   search      EmbeddingGemma 2, 4-bit ONNX (Hugging Face), ~210 MB: "Related" results, asking your documents
#   plan        Palette's fine-tuned Gemma 3 270M (this release) + llama.cpp, ~310 MB: requests in plain words
#   NPU         OpenVINO + NPU versions of the two above (this release), ~450 MB: only on Intel Core Ultra
#   chat        Gemma 4 E4B (Hugging Face) + llama.cpp's Vulkan build, ~4.9 GB: chat and "ask about this window"
# The chat model is asked about when it is not there yet. With arguments:
#
#   & ([scriptblock]::Create((irm https://raw.githubusercontent.com/aurelientrinder/colors/main/install.ps1))) -NoChat
#
#   -Chat / -NoChat   download the chat model, or not, without asking
#   -Version 0.1.0    install that release instead of the latest
#   -From <folder>    install from a folder holding the release files (make-release.ps1's dist\)
#   -InstallDir <dir> install there instead of %LOCALAPPDATA%\Programs\Palette
#   -NoStart          neither start Palette now nor at sign-in
#   -Uninstall        remove Palette (your settings and index in %LOCALAPPDATA%\palette stay)
param([switch]$Chat, [switch]$NoChat, [string]$Version, [string]$From, [string]$InstallDir, [switch]$NoStart, [switch]$Uninstall)

function Install-Palette {
    $ErrorActionPreference = "Stop"
    $ProgressPreference = "SilentlyContinue"   # Invoke-WebRequest's progress bar slows it down tenfold
    $dir = if ($InstallDir) { [IO.Path]::GetFullPath($InstallDir) } else { Join-Path $env:LOCALAPPDATA "Programs\Palette" }
    $exe = Join-Path $dir "PaletteWinUI.exe"
    $models = Join-Path $dir "models"
    $uninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Palette"
    $shortcut = Join-Path ([Environment]::GetFolderPath("Programs")) "Palette.lnk"
    $tmp = Join-Path $env:TEMP "palette-install"
    $llama = "b11609"

    function Say($text) { Write-Host $text -ForegroundColor Cyan }

    function Stop-Palette {
        # Palette and its helpers (llama-server, palette-semantic) from the install folder.
        $running = Get-Process -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -and $_.Path.StartsWith($dir + "\", [StringComparison]::OrdinalIgnoreCase) }
        $running | Stop-Process -Force -ErrorAction SilentlyContinue
        $running | Wait-Process -Timeout 15 -ErrorAction SilentlyContinue
    }

    function Remove-LogonTask {
        # Only the task that starts this copy: a "Palette" task may start a copy installed elsewhere.
        $task = Get-ScheduledTask -TaskName Palette -ErrorAction SilentlyContinue
        if ($task -and ($task.Actions | Where-Object { "$($_.Arguments)" -like "*$exe*" })) {
            Unregister-ScheduledTask -TaskName Palette -Confirm:$false
        }
    }

    if ($Uninstall) {
        Say "Removing Palette"
        Stop-Palette
        Remove-LogonTask
        Remove-Item $shortcut, $uninstallKey -Force -ErrorAction SilentlyContinue
        if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
        Write-Host "Palette is removed. Your settings and search index are still in $env:LOCALAPPDATA\palette; delete that folder too to remove everything."
        return
    }

    if (-not [Environment]::Is64BitOperatingSystem -or $env:PROCESSOR_ARCHITECTURE -eq "ARM64") {
        throw "Palette needs 64-bit Windows 10 or 11 on an Intel or AMD processor."
    }
    if (-not (Get-Command curl.exe -ErrorAction SilentlyContinue)) { throw "curl.exe is missing (it comes with Windows 10 1803 and later)." }

    # Where the release files come from.
    $base = if ($From) { (Resolve-Path $From).Path }
            elseif ($Version) { "https://github.com/aurelientrinder/colors/releases/download/v$($Version.TrimStart('v'))" }
            else { "https://github.com/aurelientrinder/colors/releases/latest/download" }
    New-Item -ItemType Directory -Force $tmp | Out-Null

    function Test-Hash($file, $sha) { (Get-FileHash $file -Algorithm SHA256).Hash -eq $sha.ToUpper() }

    # Downloads to $out unless it is there already, checked against $sha (and first $size, which is
    # quicker than hashing gigabytes). A broken-off download resumes where it stopped.
    function Get-File($url, $out, $sha, [long]$size = 0) {
        if (Test-Path $out) {
            if ($size -and (Get-Item $out).Length -eq $size) { return }
            if (-not $size -and (Test-Hash $out $sha)) { return }
        }
        $part = "$out.part"
        if ($url -notmatch "^https://") { Copy-Item $url $part -Force }
        else {
            Write-Host "  $(Split-Path $out -Leaf)"
            curl.exe -L --fail --retry 3 --progress-bar -C - -o $part $url
            if ($LASTEXITCODE -ne 0 -and -not ((Test-Path $part) -and (Test-Hash $part $sha))) {
                throw "Download failed ($LASTEXITCODE): $url"
            }
        }
        if (-not (Test-Hash $part $sha)) {
            Remove-Item $part
            throw "Download damaged (checksum mismatch): $url. Run the installer again."
        }
        Move-Item $part $out -Force
    }

    # A release file, checked against the release's SHA256SUMS.txt.
    $sumsFile = Join-Path $tmp "SHA256SUMS.txt"
    Remove-Item $sumsFile -ErrorAction SilentlyContinue
    if ($From) { Copy-Item (Join-Path $base "SHA256SUMS.txt") $sumsFile }
    else {
        curl.exe -sL --fail --retry 3 -o $sumsFile "$base/SHA256SUMS.txt"
        if ($LASTEXITCODE -ne 0) { throw "No Palette release found at $base" }
    }
    $sums = @{}
    foreach ($line in Get-Content $sumsFile) { if ($line -match "^([0-9a-f]{64})\s+\*?(.+)$") { $sums[$Matches[2]] = $Matches[1] } }
    function Get-ReleaseFile($name, $out) {
        if (-not $sums[$name]) { throw "$name is not in this release" }
        $src = if ($From) { Join-Path $base $name } else { "$base/$name" }
        Get-File $src $out $sums[$name]
        return $sums[$name]
    }

    # A zip unpacked into $dest once: a marker file holding its checksum says it is done.
    function Expand-Once($zip, $sha, $dest, $marker) {
        $m = Join-Path $dest $marker
        if ((Test-Path $m) -and (Get-Content $m) -eq $sha) { return $false }
        $x = Join-Path $tmp ([IO.Path]::GetFileNameWithoutExtension($zip))
        Remove-Item $x -Recurse -Force -ErrorAction SilentlyContinue
        Expand-Archive $zip $x
        New-Item -ItemType Directory -Force $dest | Out-Null
        return $x
    }

    function Get-Llama($kind, $sha, $dest) {
        if (Test-Path (Join-Path $dest "llama-server.exe")) { return }
        $zip = Join-Path $tmp "llama-$kind.zip"
        Get-File "https://github.com/ggml-org/llama.cpp/releases/download/$llama/llama-$llama-bin-win-$kind-x64.zip" $zip $sha
        $x = Expand-Once $zip $sha $dest ".llama"
        Copy-Item (Join-Path $x "llama-server.exe"), (Join-Path $x "*.dll") $dest
        Remove-Item $x, $zip -Recurse -Force
    }

    # Ask about the chat model first, so the rest can run unattended.
    $chatModel = Join-Path $models "chat\gemma-4-E4B-it-Q4_0.gguf"
    $wantChat = $Chat -or (Test-Path $chatModel)
    if (-not $Chat -and -not $NoChat -and -not $wantChat) {
        Write-Host "Palette can also chat and answer questions about the window in front, with a model that runs on"
        Write-Host "this PC (Gemma 4 E4B). It is a 4.9 GB download and wants 8 GB of RAM or more."
        $wantChat = (Read-Host "Download the chat model? [Y/n]") -notmatch "^\s*n"
    }

    # 1. The app.
    Say "Installing Palette in $dir"
    $zip = Join-Path $tmp "Palette-win-x64.zip"
    $sha = Get-ReleaseFile "Palette-win-x64.zip" $zip
    $x = Join-Path $tmp "app"
    Remove-Item $x -Recurse -Force -ErrorAction SilentlyContinue
    Expand-Archive $zip $x
    Stop-Palette
    # Mirrored, so files a newer version dropped go, but models\ is kept.
    robocopy $x $dir /MIR /XD models /R:3 /W:2 /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Copying to $dir failed (robocopy $LASTEXITCODE): is a file there still in use?" }
    Remove-Item $x, $zip -Recurse -Force

    # 2. Search: EmbeddingGemma 2 (Apache-2.0), at a fixed revision so the checksums hold.
    Say "Search model"
    $emb = Join-Path $models "embeddinggemma-2-text-q4"
    New-Item -ItemType Directory -Force $emb | Out-Null
    $hf = "https://huggingface.co/onnx-community/embeddinggemma-2-ONNX/resolve/daa72c51243991dfcaf9f9137d2c573d8f7790c0"
    Get-File "$hf/onnx/model_q4.onnx" (Join-Path $emb "model_q4.onnx") "f9eeba97acddf139b8ee2ddf04bc30dceafa88de93fadf74d7644e0d61a477a9" 490742
    Get-File "$hf/onnx/model_q4.onnx_data" (Join-Path $emb "model_q4.onnx_data") "c3975f2d1ab7a1878ae31a7d7a9b7804a827aff3800b60dfceafce21cac3df49" 174028800
    Get-File "$hf/tokenizer.json" (Join-Path $emb "tokenizer.json") "4d777ef5bdc1aa36227abdfb77c3e49e7b9c892d16e1b6bda41c393504828be4" 32170510
    Get-File "$hf/config.json" (Join-Path $emb "config.json") "8d011bfe08b5e345bbe0b81e5c6fd02c381920b345b986047bc2a33ce7b90d1d" 5031

    # 3. Plans: the fine-tuned model and llama.cpp's CPU build to run it.
    Say "Plan model"
    $plan = Join-Path $models "planner"
    New-Item -ItemType Directory -Force $plan | Out-Null
    Get-Llama "cpu" "8708a7b81842333927f6377ac3f17ff8bba4dbe1ee0cf28dfd5e7a32e3a46b38" $plan
    Get-ReleaseFile "plan.gguf" (Join-Path $plan "plan.gguf") | Out-Null

    # 4. The NPU (Intel Core Ultra's "AI Boost"): OpenVINO's runtime, and the NPU versions of both models.
    $npu = Get-CimInstance Win32_PnPEntity -Filter "PNPClass='ComputeAccelerator'" -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match "AI Boost|Intel.*NPU" }
    if ($npu) {
        Say "NPU ($($npu[0].Name))"
        $ov = Join-Path $models "openvino"
        if (-not (Test-Path (Join-Path $ov "openvino_c.dll"))) {
            # The DLLs of OpenVINO's Python wheel (Apache-2.0); only the NPU plugin, ~25 MB once unpacked.
            $meta = Invoke-RestMethod "https://pypi.org/pypi/openvino/2026.4.1/json"
            $wheel = $meta.urls | Where-Object { $_.filename -like "*win_amd64.whl" } | Select-Object -First 1
            $whl = Join-Path $tmp "openvino.zip"
            Get-File $wheel.url $whl $wheel.digests.sha256
            $x = Expand-Once $whl $wheel.digests.sha256 $ov ".openvino"
            foreach ($d in "openvino.dll", "openvino_c.dll", "openvino_intel_npu_plugin.dll", "openvino_ir_frontend.dll", "tbb12.dll", "tbbmalloc.dll") {
                Copy-Item (Join-Path $x "openvino\libs\$d") $ov
            }
            Remove-Item $x, $whl -Recurse -Force
        }
        $zip = Join-Path $tmp "palette-npu.zip"
        $sha = Get-ReleaseFile "palette-npu.zip" $zip
        $x = Expand-Once $zip $sha $models ".npu"
        if ($x) {
            Copy-Item (Join-Path $x "*") $models -Recurse -Force
            Set-Content (Join-Path $models ".npu") $sha
            Remove-Item $x -Recurse -Force
        }
        Remove-Item $zip -ErrorAction SilentlyContinue
    }

    # 5. Chat: Gemma 4 E4B (Apache-2.0) with its multi-token-prediction draft, which makes it about a third
    # quicker, and llama.cpp's Vulkan build to run them on the GPU.
    if ($wantChat) {
        Say "Chat model"
        $chat = Join-Path $models "chat"
        New-Item -ItemType Directory -Force $chat | Out-Null
        Get-Llama "vulkan" "7b50bdb2a27b5fe17759104b1d86e082a05583ff1bca8b3202bcfb2a7232a005" $chat
        $hf = "https://huggingface.co/unsloth/gemma-4-E4B-it-GGUF/resolve/bfc15c382204943c3a8fff0c750b94ae2364d7a3"
        Get-File "$hf/mtp-gemma-4-E4B-it.gguf" (Join-Path $chat "mtp-gemma-4-E4B-it.gguf") "b6a723115efa510d3b3215db1e26790dae84cd08c2134a764f3d194f1f0c3376" 98653248
        Get-File "$hf/gemma-4-E4B-it-Q4_0.gguf" $chatModel "4a403d2e4d80281063e4f517b1c061ded8476b4011a4fc2ba7dbff707075547e" 4836002944
    }

    # 6. Start menu, sign-in, Apps & features. The logon task is the one Palette's own "Run at startup" makes.
    Say "Adding Palette to Windows"
    $lnk = (New-Object -ComObject WScript.Shell).CreateShortcut($shortcut)
    $lnk.TargetPath = $exe
    $lnk.WorkingDirectory = $dir
    $lnk.Description = "Palette"
    $lnk.Save()

    $u = "$env:USERDOMAIN\$env:USERNAME"
    if (-not $NoStart) { Register-ScheduledTask -TaskName Palette -Force `
        -Action (New-ScheduledTaskAction -Execute "rundll32.exe" -Argument "shell32.dll,ShellExec_RunDLL `"$exe`"" -WorkingDirectory $dir) `
        -Trigger (New-ScheduledTaskTrigger -AtLogOn -User $u) `
        -Principal (New-ScheduledTaskPrincipal -UserId $u -LogonType Interactive -RunLevel Limited) `
        -Settings (New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit 0 -MultipleInstances IgnoreNew -Priority 4) | Out-Null }

    $version = (Get-Content (Join-Path $dir "version.txt") -ErrorAction SilentlyContinue) -join ""
    $kb = [int]((Get-ChildItem $dir -Recurse -File | Measure-Object Length -Sum).Sum / 1KB)
    New-Item $uninstallKey -Force | Out-Null
    $values = @{
        DisplayName = "Palette"; DisplayVersion = $version; Publisher = "Aurelien Trinder"; DisplayIcon = $exe
        InstallLocation = $dir; NoModify = 1; NoRepair = 1; EstimatedSize = $kb
        UninstallString = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$dir\install.ps1`" -Uninstall -InstallDir `"$dir`""
        URLInfoAbout = "https://github.com/aurelientrinder/colors"
    }
    foreach ($k in $values.Keys) { Set-ItemProperty $uninstallKey $k $values[$k] }

    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
    if ($NoStart) { Say "Palette $version is installed in $dir."; return }
    Start-Process $exe -WorkingDirectory $dir
    Say "Palette $version is installed and running. Press the Windows key to open it."
    if (-not $wantChat) { Write-Host "Run the installer again with -Chat to add the chat model later." }
}

Install-Palette
