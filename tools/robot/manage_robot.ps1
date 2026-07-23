param(
    [string]$RobotHost,
    [string]$RobotUser,
    [string]$RemoteDirectory,
    [string]$Password,
    [string]$ConfigFile,
    [switch]$Deploy,
    [switch]$StartStealth,
    [switch]$StartNormal,
    [switch]$Status,
    [switch]$Stop
)

$ErrorActionPreference = "Stop"

$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..\..")).Path
if (-not $ConfigFile) {
    $ConfigFile = Join-Path $projectRoot ".robot.config.psd1"
}

if (Test-Path -LiteralPath $ConfigFile) {
    $config = Import-PowerShellDataFile -LiteralPath $ConfigFile
    if (-not $PSBoundParameters.ContainsKey("RobotHost") -and $config.RobotHost) {
        $RobotHost = [string]$config.RobotHost
    }
    if (-not $PSBoundParameters.ContainsKey("RobotUser") -and $config.RobotUser) {
        $RobotUser = [string]$config.RobotUser
    }
    if (-not $PSBoundParameters.ContainsKey("RemoteDirectory") -and $config.RemoteDirectory) {
        $RemoteDirectory = [string]$config.RemoteDirectory
    }
    if (-not $PSBoundParameters.ContainsKey("Password") -and $config.Password) {
        $Password = [string]$config.Password
    }
}

if (-not $RobotHost) { $RobotHost = "192.168.2.158" }
if (-not $RobotUser) { $RobotUser = "pi" }
if (-not $RemoteDirectory) { $RemoteDirectory = "/home/pi/team1.1" }
if (-not $Password) { $Password = $env:ROBOT_PASSWORD }

if (-not $Password) {
    $securePassword = Read-Host "Password for $RobotUser@$RobotHost" -AsSecureString
    $credential = [System.Net.NetworkCredential]::new("", $securePassword)
    $Password = $credential.Password
}

$puttyRoot = Join-Path $env:ProgramFiles "PuTTY"
$plink = Join-Path $puttyRoot "plink.exe"
$pscp = Join-Path $puttyRoot "pscp.exe"
if (-not (Test-Path -LiteralPath $plink) -or -not (Test-Path -LiteralPath $pscp)) {
    throw "PuTTY plink.exe and pscp.exe are required in $puttyRoot"
}

$sourceDirectory = Join-Path $projectRoot "robot\team1.1"
$remote = "$RobotUser@$RobotHost"

function Invoke-Plink([string]$Command) {
    & $plink -batch -pw $Password $remote $Command
    if ($LASTEXITCODE -ne 0) {
        throw "Remote command failed with exit code $LASTEXITCODE"
    }
}

if ($Deploy) {
    $files = @(
        "README.md",
        "unity_master_team1.py",
        "robot_hardware.py",
        "manual_robot.py",
        "test_robot_hardware.py",
        "hardware_pinout.py",
        "detect_ir_pin.py",
        "camera_stream_team1.py",
        "start_robot_team1.1.sh",
        "stop_robot_team1.1.sh",
        "status_robot_team1.1.sh",
        "smbus.py",
        "xr_car_light.py",
        "xr_music.py"
    ) | ForEach-Object { Join-Path $sourceDirectory $_ }

    Invoke-Plink "mkdir -p '$RemoteDirectory'"
    & $pscp -batch -pw $Password @files "${remote}:${RemoteDirectory}/"
    if ($LASTEXITCODE -ne 0) {
        throw "Deployment failed with exit code $LASTEXITCODE"
    }
    Invoke-Plink "cd '$RemoteDirectory' && sed -i 's/\r$//' *.sh && chmod +x *.sh *.py && bash -n *.sh && python3 -m py_compile unity_master_team1.py robot_hardware.py manual_robot.py hardware_pinout.py camera_stream_team1.py test_robot_hardware.py && python3 -m unittest -v test_robot_hardware.py"
    Write-Host "Robot files deployed and syntax-checked."
}

if ($Stop) {
    Invoke-Plink "cd '$RemoteDirectory' && ./stop_robot_team1.1.sh"
}

if ($StartStealth) {
    Invoke-Plink "cd '$RemoteDirectory' && ./stop_robot_team1.1.sh && ./start_robot_team1.1.sh --stealth"
}

if ($StartNormal) {
    Write-Warning "Normal mode initializes and moves physical servos. Keep the robot lifted and the area clear."
    Invoke-Plink "cd '$RemoteDirectory' && ./stop_robot_team1.1.sh && ./start_robot_team1.1.sh"
}

if ($Status) {
    Invoke-Plink "cd '$RemoteDirectory' && ./status_robot_team1.1.sh"
}
