param(
    [Parameter(Mandatory = $true)][guid]$OrderId,
    [guid]$CustomerProfileId,
    [Parameter(Mandatory = $true)][uri]$ApiBaseUrl
)
$ErrorActionPreference = 'Stop'
if ($ApiBaseUrl.Scheme -ne 'https' -and !$ApiBaseUrl.IsLoopback) { throw 'Use HTTPS for non-local APIs.' }
if ([string]::IsNullOrWhiteSpace($env:DIETTIME_CUSTOMER_ACCESS_TOKEN)) { throw 'Set DIETTIME_CUSTOMER_ACCESS_TOKEN in your environment to the affected customer session token.' }
$taskHeaders = @{ Authorization = 'Bearer ' + $env:DIETTIME_CUSTOMER_ACCESS_TOKEN }
$taskBase = $ApiBaseUrl.AbsoluteUri.TrimEnd('/')
$taskProfile = (Invoke-RestMethod -Uri ($taskBase + '/api/v1/customer/profile') -Headers $taskHeaders).data
if (!$taskProfile.id) { throw 'The authenticated customer profile could not be resolved.' }
if ($CustomerProfileId -ne [guid]::Empty -and $taskProfile.id -ne $CustomerProfileId.ToString()) { throw 'The supplied customer profile does not match the authenticated session.' }
$CustomerProfileId = [guid]$taskProfile.id
Write-Output ('Authenticated UserId=' + $taskProfile.userId + ' CustomerProfileId=' + $CustomerProfileId)
$taskPageNumber = 1
$taskOrder = $null
do {
    $taskPage = Invoke-RestMethod -Uri ($taskBase + '/api/v1/customer-profiles/' + $CustomerProfileId + '/orders?pageSize=100&pageNumber=' + $taskPageNumber) -Headers $taskHeaders
    $taskOrder = @($taskPage.items | Where-Object { $_.id -eq $OrderId.ToString() }) | Select-Object -First 1
    if (!$taskOrder -and @($taskPage.items).Count -eq 0 -and ($taskPageNumber - 1) * 100 -lt $taskPage.totalCount) { throw 'Customer order pagination is incomplete.' }
    $taskPageNumber++
} while (!$taskOrder -and ($taskPageNumber - 1) * 100 -lt $taskPage.totalCount)
if (!$taskOrder) { throw 'The authenticated customer order list does not contain this order. Check the profile, order identifier, and environment.' }
$taskOrder | Select-Object id, orderNumber, status, planName, startDate, endDate | ConvertTo-Json
$taskResponse = Invoke-RestMethod -Uri ($taskBase + '/api/v1/orders/' + $OrderId + '/tomorrow-menu?language=en') -Headers $taskHeaders
$taskMenu = $taskResponse.data
$taskMenu | ConvertTo-Json -Depth 10
if ($taskMenu.subscriptionId -ne $OrderId.ToString()) { throw 'The API resolved a different order identifier.' }
if (!$taskMenu.canConfirm) { throw ('The real order was found but an eligible open menu was not verified. Status: ' + $taskMenu.selectionStatus + '. Reason: ' + $taskMenu.emptyReason) }
if (!$taskMenu.subscriptionServiceDayId -or @($taskMenu.availableMeals).Count -lt $taskMenu.mealCount -or @($taskMenu.availableSnacks).Count -lt $taskMenu.snackCount) { throw 'The response is missing its service day or purchased dish quantities.' }
Write-Output 'Verified: this authenticated existing order loads its eligible Tomorrow menu. No selections were changed.'
