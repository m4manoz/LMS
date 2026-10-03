<#
Starts a LiveKit server with its recording service (Egress) and Redis in Docker, for trying out live classes and class recordings on this machine.

  .\scripts\livekit-dev.ps1            start (or restart) everything
  .\scripts\livekit-dev.ps1 -Stop      remove it again

Keys are devkey / secret. The server listens on ws://localhost:7880.
Recordings are written to the folder given by -OutputDirectory (default src\Lms.Web\.e2e\egress), which the API reads: start the API with
  LiveKit__Egress__Enabled=true  LiveKit__Egress__Destination=Local  LiveKit__Egress__LocalDirectory=<that folder>
The browser tests use the same folder; run them with E2E_EGRESS=1.

-HostIp is the address of this machine that both your browser and the Docker containers can reach (the recording service joins the room through it).
It is found automatically (the first private 192.168.x / 10.x address) when not given.
#>
param(
  [switch]$Stop,
  [string]$HostIp = "",
  [string]$OutputDirectory = ""
)

$ErrorActionPreference = "Stop"
$names = @("lms-lk-egress", "lms-livekit-e2e", "lms-lk-redis")

# Removing something that is not there is not an error, so errors are ignored here (Docker reports them on stderr).
function Remove-Containers {
  $previous = $ErrorActionPreference
  $ErrorActionPreference = "Continue"
  foreach ($name in $names) { docker rm -f $name 2>&1 | Out-Null }
  docker network rm lms-lk-net 2>&1 | Out-Null
  $ErrorActionPreference = $previous
}

# Runs a Docker command and stops with its message if it fails.
function Invoke-Docker {
  $output = docker @args 2>&1
  if ($LASTEXITCODE -ne 0) { throw "docker $($args -join ' ') failed: $output" }
}

Remove-Containers
if ($Stop) { Write-Host "Stopped."; return }

if (-not $HostIp) {
  $HostIp = (Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.IPAddress -match '^(192\.168\.|10\.)' -and $_.IPAddress -notmatch '^192\.168\.(56|65)\.' } | Select-Object -First 1).IPAddress
  if (-not $HostIp) { throw "Could not find this machine's address. Pass -HostIp." }
}
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot "..\src\Lms.Web\.e2e\egress" }
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$OutputDirectory = (Resolve-Path $OutputDirectory).Path

Invoke-Docker network create lms-lk-net
Invoke-Docker run -d --name lms-lk-redis --network lms-lk-net redis:7-alpine
Invoke-Docker run -d --name lms-livekit-e2e --network lms-lk-net --network-alias livekit -p 7880:7880 -p 7881:7881 -p 7882:7882/udp livekit/livekit-server --dev --bind 0.0.0.0 --node-ip $HostIp --redis-host lms-lk-redis:6379

$config = @"
api_key: devkey
api_secret: secret
ws_url: ws://livekit:7880
redis:
  address: lms-lk-redis:6379
health_port: 8080
log_level: info
"@
Invoke-Docker run -d --name lms-lk-egress --network lms-lk-net --cap-add SYS_ADMIN -e "EGRESS_CONFIG_BODY=$config" -v "${OutputDirectory}:/out" livekit/egress

Write-Host "LiveKit:  ws://localhost:7880  (keys devkey / secret, reachable by containers as $HostIp)"
Write-Host "Recordings folder: $OutputDirectory"
