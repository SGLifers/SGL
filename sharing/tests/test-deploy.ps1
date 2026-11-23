$ErrorActionPreference = 'Stop'
# Exercise the actual deployment script with a fake CLI in an isolated tree.
# This test never invokes the installed AWS executable or accesses credentials.
$source = Split-Path $PSScriptRoot -Parent
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('sgl-deploy-test-' + [guid]::NewGuid().ToString('N'))
$infra = Join-Path $fixture 'sharing/infra'
New-Item -ItemType Directory -Force -Path $infra,(Join-Path $fixture 'sharing/backend'),(Join-Path $fixture 'src/SGL.Core/Data') | Out-Null
Copy-Item -LiteralPath (Join-Path $source 'infra/deploy.ps1'),(Join-Path $source 'infra/template.json') -Destination $infra
Copy-Item -Path (Join-Path $source 'backend/*.py') -Destination (Join-Path $fixture 'sharing/backend')
$global:SglDeploymentMockCalls = [Collections.Generic.List[object]]::new()
$global:SglDeploymentMockAccount = '111111111111'
function global:aws {
    $global:SglDeploymentMockCalls.Add([string[]]$args)
    $global:LASTEXITCODE = 0
    if ($args[0] -eq 'sts') { return (@{ Account = $global:SglDeploymentMockAccount } | ConvertTo-Json -Compress) }
    if ($args[0] -eq 's3api' -and $args[1] -eq 'head-bucket') { $global:LASTEXITCODE = 1; return }
    if ($args[0] -eq 'cloudformation' -and $args[1] -eq 'describe-stacks') {
        return '[{"OutputKey":"ApiUrl","OutputValue":"https://mock.execute-api.us-west-2.amazonaws.com"},{"OutputKey":"DiscordCallbackUrl","OutputValue":"https://mock.execute-api.us-west-2.amazonaws.com/auth/callback"}]'
    }
    return '{}'
}
try {
    $deploy = Join-Path $infra 'deploy.ps1'
    & $deploy -Profile MOCK_ONLY -AccountId 111111111111 -Region us-west-2 -PlanOnly
    if ($global:SglDeploymentMockCalls.Count -ne 2 -or $global:SglDeploymentMockCalls[0][0] -ne 'sts' -or $global:SglDeploymentMockCalls[1][1] -ne 'validate-template') { throw 'Plan performed unexpected resource calls.' }
    if (Test-Path -LiteralPath (Join-Path $fixture 'src/SGL.Core/Data/library-service.json')) { throw 'Plan changed app configuration.' }
    $global:SglDeploymentMockCalls.Clear()
    $global:SglDeploymentMockAccount = '222222222222'
    try { & $deploy -Profile MOCK_ONLY -AccountId 111111111111 -PlanOnly; throw 'Account mismatch was accepted.' }
    catch { if ($_.Exception.Message -notlike '*different account*') { throw } }
    if ($global:SglDeploymentMockCalls.Count -ne 1) { throw 'Account mismatch did not stop before resource calls.' }
    $global:SglDeploymentMockCalls.Clear()
    $global:SglDeploymentMockAccount = '111111111111'
    & $deploy -Profile MOCK_ONLY -AccountId 111111111111 -Region us-west-2
    foreach ($call in $global:SglDeploymentMockCalls) {
        $profile = [Array]::IndexOf($call, '--profile'); $region = [Array]::IndexOf($call, '--region')
        if ($profile -lt 0 -or $call[$profile + 1] -ne 'MOCK_ONLY' -or $region -lt 0 -or $call[$region + 1] -ne 'us-west-2') { throw 'Deployment lost the explicit profile or region.' }
    }
    if (!($global:SglDeploymentMockCalls | Where-Object { $_[0] -eq 's3api' -and $_[1] -eq 'create-bucket' -and $_ -contains 'LocationConstraint=us-west-2' })) { throw 'New bucket lacks the target location.' }
    if (!($global:SglDeploymentMockCalls | Where-Object { $_[0] -eq 'cloudformation' -and $_[1] -eq 'deploy' -and $_ -contains 'CAPABILITY_IAM' })) { throw 'Stack deployment arguments are invalid.' }
    $configuration = Get-Content -LiteralPath (Join-Path $fixture 'src/SGL.Core/Data/library-service.json') -Raw | ConvertFrom-Json
    if ($configuration.apiUrl -ne 'https://mock.execute-api.us-west-2.amazonaws.com') { throw 'Deployment did not embed the API output.' }
    Write-Output 'Passed mocked deployment: plan, account guard, ZIP, profile/region propagation, bucket creation, stack and endpoint outputs. No real AWS calls.'
}
finally {
    Remove-Item -LiteralPath 'Function:\aws' -ErrorAction SilentlyContinue
    Remove-Variable SglDeploymentMockCalls,SglDeploymentMockAccount -Scope Global -ErrorAction SilentlyContinue
}
