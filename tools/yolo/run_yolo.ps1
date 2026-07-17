param(
    [string]$StreamUrl = "",
    [string]$FallbackStreamUrl = "",
    [string]$Model = "",
    [ValidateSet("auto", "stream", "snapshot")]
    [string]$SourceMode = "auto",
    [switch]$SetupOnly,
    [switch]$NoDisplay,
    [switch]$NoTrack
)

$ErrorActionPreference = "Stop"
$toolRoot = $PSScriptRoot
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $toolRoot "..\..")).Path
$venvRoot = Join-Path $toolRoot ".venv"
$python = Join-Path $venvRoot "Scripts\python.exe"

if (-not (Test-Path -LiteralPath $python)) {
    Write-Host "Creating YOLO virtual environment..."
    $environmentCreated = $false
    $pythonCommand = Get-Command python -ErrorAction SilentlyContinue
    if ($pythonCommand) {
        & $pythonCommand.Source -m venv $venvRoot
        $environmentCreated = $LASTEXITCODE -eq 0
    }

    if (-not $environmentCreated) {
        $pyLauncher = Get-Command py -ErrorAction SilentlyContinue
        if ($pyLauncher) {
            & $pyLauncher.Source -3 -m venv $venvRoot
            $environmentCreated = $LASTEXITCODE -eq 0
        }
    }

    if (-not $environmentCreated) {
        throw "Python 3 was not found. Install it and add either 'python' or 'py' to PATH."
    }
}

& $python -c "import cv2, onnxruntime" 2>$null
if ($LASTEXITCODE -ne 0) {
    Write-Host "Installing YOLO dependencies (first launch only)..."
    & $python -m pip install --upgrade pip
    if ($LASTEXITCODE -ne 0) { throw "Failed to upgrade pip in the YOLO environment." }
    & $python -m pip install -r (Join-Path $toolRoot "requirements.txt")
    if ($LASTEXITCODE -ne 0) { throw "Failed to install YOLO dependencies." }
}

if ($SetupOnly) {
    Write-Host "YOLO environment is ready: $python"
    exit 0
}

$arguments = @(
    (Join-Path $toolRoot "yolo_vision_node.py"),
    "--source-mode", $SourceMode
)

if ($StreamUrl) { $arguments += @("--stream-url", $StreamUrl) }
if ($FallbackStreamUrl) { $arguments += @("--fallback-stream-url", $FallbackStreamUrl) }
if ($Model) { $arguments += @("--model", $Model) }
if ($NoDisplay) { $arguments += "--no-display" }
if ($NoTrack) { $arguments += "--no-track" }

Push-Location $projectRoot
$nodeExitCode = 1
try {
    & $python @arguments
    $nodeExitCode = $LASTEXITCODE
}
finally {
    Pop-Location
}

exit $nodeExitCode
