param([Parameter(Mandatory)][string]$ApiUrl)
$ErrorActionPreference = 'Stop'
$uri = [Uri]$ApiUrl
if ($uri.Scheme -ne 'https' -or $uri.UserInfo -or $uri.Query -or $uri.Fragment -or !$uri.IsDefaultPort -or $uri.AbsolutePath.Trim('/') -ne '') { throw 'Use the HTTPS statistics API root.' }
$health = Invoke-RestMethod -Uri ($ApiUrl.TrimEnd('/') + '/health') -TimeoutSec 15
if ($health.service -ne 'SplitStats' -or 1 -notin $health.schemaVersions) { throw 'Unexpected statistics service.' }
$path = Join-Path (Split-Path $PSScriptRoot -Parent) 'src/SGL.Core/Data/statistics-service.json'
@{ apiUrl = $ApiUrl.TrimEnd('/') } | ConvertTo-Json -Compress | Set-Content -Path $path -Encoding utf8
Write-Output 'Statistics endpoint configured. Rebuild SGL to use it. No credentials were embedded.'
