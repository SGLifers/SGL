param([Parameter(Mandatory)][string]$ApiUrl,[switch]$ValidateOnly)
$ErrorActionPreference='Stop'
$uri=$null
if(-not [Uri]::TryCreate($ApiUrl,[UriKind]::Absolute,[ref]$uri) -or $uri.Scheme -ne 'https' -or $uri.UserInfo -or $uri.Query -or $uri.Fragment -or -not $uri.IsDefaultPort){throw 'Use the HTTPS ApiUrl output from the deployed SGL hosting stack.'}
$target=Join-Path (Split-Path $PSScriptRoot -Parent) 'src/SGL.Core/Data/hosting-service.json'
if($ValidateOnly){Write-Output 'Hosting endpoint format is valid. No files changed.';return}
$health=Invoke-RestMethod -Uri ($ApiUrl.TrimEnd('/')+'/health')
if($health.service -ne 'SGL Hosting'){throw 'The endpoint did not identify itself as SGL Hosting.'}
[IO.File]::WriteAllText($target,(@{apiUrl=$ApiUrl.TrimEnd('/')}|ConvertTo-Json),(New-Object Text.UTF8Encoding($false)))
Write-Output 'Hosting endpoint configured. Rebuild and publish SGL to connect users. No message was sent.'
