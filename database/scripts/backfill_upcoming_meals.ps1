param(
    [Parameter(Mandatory = $true)][guid]$OrderId,
    [Parameter(Mandatory = $true)][uri]$ApiBaseUrl,
    [switch]$Apply
)
$ErrorActionPreference = 'Stop'
if ($ApiBaseUrl.Scheme -ne 'https' -and !$ApiBaseUrl.IsLoopback) { throw 'Use HTTPS for non-local APIs.' }
if ([string]::IsNullOrWhiteSpace($env:DIETTIME_ADMIN_ACCESS_TOKEN)) { throw 'Set DIETTIME_ADMIN_ACCESS_TOKEN to an authorized Admin/Operations access token.' }
$taskHeaders = @{ Authorization = 'Bearer ' + $env:DIETTIME_ADMIN_ACCESS_TOKEN }
$taskEndpoint = $ApiBaseUrl.AbsoluteUri.TrimEnd('/') + '/api/v1/admin/orders/' + $OrderId + '/initialize-upcoming-meals'
$taskPreview = Invoke-RestMethod -Method Get -Uri $taskEndpoint -Headers $taskHeaders
$taskPreview | ConvertTo-Json -Depth 10
if (!$Apply) { Write-Output 'Preview only. Verify this order, then repeat with -Apply to initialize missing current/future service days.'; return }
if (!$taskPreview.canInitialize) { throw 'This order is ineligible or has link/sequence conflicts. No backfill was requested.' }
Invoke-RestMethod -Method Post -Uri $taskEndpoint -Headers $taskHeaders | ConvertTo-Json -Depth 5
Invoke-RestMethod -Method Get -Uri $taskEndpoint -Headers $taskHeaders | ConvertTo-Json -Depth 10
