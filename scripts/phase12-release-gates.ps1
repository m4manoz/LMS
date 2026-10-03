[CmdletBinding()]
param(
    [string]$ApiBaseUrl = 'http://localhost:5106',
    [string]$TenantSlug = 'acme',
    [string]$Email = 'admin@acme.test',
    [string]$Password = $env:LMS_GATE_PASSWORD
)

if ([string]::IsNullOrWhiteSpace($Password)) { throw 'Pass -Password or set LMS_GATE_PASSWORD; no default password is stored in this script.' }

$ErrorActionPreference = 'Stop'
$jsonHeaders = @{ 'Content-Type' = 'application/json' }

function Invoke-ApiJson([string]$Method, [string]$Path, [object]$Body = $null, [hashtable]$ExtraHeaders = @{}) {
    $headers = @{} + $jsonHeaders + $ExtraHeaders
    $uri = "$ApiBaseUrl$Path"
    if ($null -eq $Body) { return Invoke-RestMethod -Method $Method -Uri $uri -Headers $headers }
    return Invoke-RestMethod -Method $Method -Uri $uri -Headers $headers -Body ($Body | ConvertTo-Json -Depth 10)
}

function Assert-Equal($Expected, $Actual, [string]$Message) {
    if ($Expected -ne $Actual) { throw "$Message Expected '$Expected', got '$Actual'." }
    Write-Output "PASS: $Message"
}

function Get-ApiStatus([string]$Method, [string]$Path, [hashtable]$Headers, [object]$Body = $null) {
    try {
        $request = @{ Method = $Method; Uri = "$ApiBaseUrl$Path"; Headers = $Headers; ErrorAction = 'Stop' }
        if ($null -ne $Body) { $request.Body = $Body | ConvertTo-Json -Depth 10; $request.ContentType = 'application/json' }
        $response = Invoke-WebRequest @request
        return [int]$response.StatusCode
    }
    catch {
        if ($_.Exception.Response) { return [int]$_.Exception.Response.StatusCode.value__ }
        throw
    }
}

$login = Invoke-ApiJson 'POST' '/api/v1/auth/login' @{ tenantSlug = $TenantSlug; email = $Email; password = $Password }
$authHeaders = @{ Authorization = "Bearer $($login.accessToken)"; 'X-Tenant-Slug' = $TenantSlug }
Write-Output 'PASS: authenticated tenant login'
$summary = Invoke-ApiJson 'GET' '/api/v1/tenant/operations/summary' $null $authHeaders
if ($null -eq $summary.devicePilot -or $null -eq $summary.counts) { throw 'Operations summary did not return readiness evidence.' }
Write-Output 'PASS: operations summary returns counts, telemetry, alerts, and pilot decision'

$allDevices = Invoke-ApiJson 'GET' '/api/v1/tenant/offline/devices' $null $authHeaders
$devices = @($allDevices | ForEach-Object { if ($_.status -eq 'Active') { $_ } })
$newDevices = @()
while ($devices.Count -lt 3) {
    $fingerprint = "phase12-release-$([Guid]::NewGuid().ToString('N'))"
    $created = Invoke-ApiJson 'POST' '/api/v1/tenant/offline/devices' @{ name = 'Phase 12 release gate'; fingerprint = $fingerprint } $authHeaders
    $newDevices += $created
    $devices += $created
}

$limitHeaders = @{} + $jsonHeaders + $authHeaders
$limitStatus = Get-ApiStatus 'POST' '/api/v1/tenant/offline/devices' $limitHeaders @{ name = 'Phase 12 limit probe'; fingerprint = "phase12-limit-$([Guid]::NewGuid().ToString('N'))" }
Assert-Equal 409 $limitStatus 'three-device active limit'

if ($newDevices.Count -gt 0) {
    $revokedId = $newDevices[0].id
    Invoke-ApiJson 'POST' "/api/v1/tenant/offline/devices/$revokedId/revoke" @{} $authHeaders | Out-Null
    $listed = Invoke-ApiJson 'GET' '/api/v1/tenant/offline/devices' $null $authHeaders
    $revoked = $listed | Where-Object { $_.id -eq $revokedId }
    Assert-Equal 'Revoked' $revoked.status 'device revocation'
}

$wrongTenantHeaders = @{} + $jsonHeaders + @{ Authorization = "Bearer $($login.accessToken)"; 'X-Tenant-Slug' = 'tenant-that-does-not-exist' }
$wrongTenantStatus = Get-ApiStatus 'GET' '/api/v1/tenant/operations/summary' $wrongTenantHeaders
if ($wrongTenantStatus -lt 400) { throw 'Wrong-tenant request unexpectedly succeeded.' }
Write-Output "PASS: wrong-tenant request rejected with HTTP $wrongTenantStatus"

foreach ($device in $newDevices) {
    Invoke-ApiJson 'POST' "/api/v1/tenant/offline/devices/$($device.id)/revoke" @{} $authHeaders -ErrorAction SilentlyContinue | Out-Null
}
Write-Output 'Phase 12 release gates completed.'
