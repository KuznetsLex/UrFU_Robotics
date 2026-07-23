param(
    [string]$RobotHost,
    [string]$RobotUser,
    [string]$RemoteDirectory,
    [string]$Password,
    [string]$HostKey,
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
    if (-not $PSBoundParameters.ContainsKey("HostKey") -and $config.HostKey) {
        $HostKey = [string]$config.HostKey
    }
}

function Find-Robot158 {
    $candidates = [System.Collections.Generic.HashSet[string]]::new()
    foreach ($network in Get-NetIPConfiguration) {
        if (-not $network.IPv4DefaultGateway) { continue }
        foreach ($address in $network.IPv4Address.IPAddress) {
            $octets = $address.Split(".")
            if ($octets.Count -ne 4) { continue }
            foreach ($hostIndex in 1..254) {
                if ($hostIndex -ne [int]$octets[3]) {
                    [void]$candidates.Add("$($octets[0]).$($octets[1]).$($octets[2]).$hostIndex")
                }
            }
        }
    }

    $probes = foreach ($address in $candidates) {
        $client = [System.Net.Sockets.TcpClient]::new()
        [pscustomobject]@{
            Address = $address
            Client = $client
            Task = $client.ConnectAsync($address, 10003)
        }
    }

    Start-Sleep -Milliseconds 800
    try {
        foreach ($probe in $probes) {
            if (-not $probe.Task.IsCompleted -or -not $probe.Client.Connected) { continue }
            $reader = [System.IO.StreamReader]::new($probe.Client.GetStream())
            try {
                if ($reader.ReadLine() -eq "ROBOCAMP_158") {
                    return $probe.Address
                }
            }
            finally {
                $reader.Dispose()
            }
        }
    }
    finally {
        foreach ($probe in $probes) {
            $probe.Client.Dispose()
        }
    }

    throw "Robot 158 was not found on a directly connected IPv4 subnet."
}

if (-not $RobotHost) { $RobotHost = Find-Robot158 }
if (-not $RobotUser) { $RobotUser = "pi" }
if (-not $RemoteDirectory) { $RemoteDirectory = "/home/pi/team1.1" }
if (-not $Password) { $Password = $env:ROBOT_PASSWORD }
if (-not $HostKey) { $HostKey = "SHA256:FO1zWe+34VEAVa953e97cmQIARdbSAd4uDC8qHflpS4" }

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
    & $plink -batch -hostkey $HostKey -pw $Password $remote $Command
    if ($LASTEXITCODE -ne 0) {
        throw "Remote command failed with exit code $LASTEXITCODE"
    }
}

if ($Deploy) {
    $files = @(
        "README.md",
        "unity_master_team1.py",
        "hardware_pinout.py",
        "detect_ir_pin.py",
        "camera_stream_team1.py",
        "robocamp_discovery.py",
        "robocamp-discovery.service",
        "start_robot_team1.1.sh",
        "stop_robot_team1.1.sh",
        "status_robot_team1.1.sh",
        "smbus.py",
        "xr_car_light.py",
        "xr_music.py"
    ) | ForEach-Object { Join-Path $sourceDirectory $_ }

    Invoke-Plink "mkdir -p '$RemoteDirectory'"
    & $pscp -batch -hostkey $HostKey -pw $Password @files "${remote}:${RemoteDirectory}/"
    if ($LASTEXITCODE -ne 0) {
        throw "Deployment failed with exit code $LASTEXITCODE"
    }
    # PuTTY pscp copies bytes verbatim. Normalize Windows line endings on the
    # Linux target before executing scripts, even if a local checkout ignored
    # the repository's eol attributes.
    Invoke-Plink "cd '$RemoteDirectory' && sed -i 's/\r$//' *.sh *.py && chmod +x *.sh *.py && bash -n *.sh && python3 -m py_compile unity_master_team1.py hardware_pinout.py camera_stream_team1.py"
    Invoke-Plink "sudo -n install -o root -g root -m 0755 '$RemoteDirectory/robocamp_discovery.py' /usr/local/lib/robocamp_discovery.py && sudo -n install -o root -g root -m 0644 '$RemoteDirectory/robocamp-discovery.service' /etc/systemd/system/robocamp-discovery.service && sudo -n systemctl daemon-reload && sudo -n systemctl enable --now robocamp-discovery.service"
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
