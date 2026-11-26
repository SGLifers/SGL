$ErrorActionPreference = 'Stop'
# The aws function mock must emit an unwrapped CLI-like diagnostic on every host.
$previousErrorView = $ErrorView
$ErrorView = 'NormalView'
# No real AWS executable or credentials are used by this test.
$source = Split-Path $PSScriptRoot -Parent
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('sgl-deploy-test-' + [guid]::NewGuid().ToString('N'))
$infra = Join-Path $fixture 'sharing/infra'
New-Item -ItemType Directory -Force -Path $infra,(Join-Path $fixture 'sharing/backend'),(Join-Path $fixture 'src/SGL.Core/Data') | Out-Null
Copy-Item -Path (Join-Path $source 'infra/*') -Destination $infra
Copy-Item -Path (Join-Path $source 'backend/*.py') -Destination (Join-Path $fixture 'sharing/backend')
$global:SglDeploymentMockCalls = [Collections.Generic.List[object]]::new()
$global:SglMock = @{ Account='111111111111'; Concurrency=1001; Deployed=$false; Paused=$false; FailControl=$false; InvalidStatus=$false }
function global:aws {
    $global:SglDeploymentMockCalls.Add([string[]]$args)
    $global:LASTEXITCODE = 0
    if ($args[0] -eq 'sts') { return (@{ Account = $global:SglMock.Account } | ConvertTo-Json -Compress) }
    if ($args[0] -eq 'lambda' -and $args[1] -eq 'get-account-settings') { return (@{ConcurrentExecutions=$global:SglMock.Concurrency} | ConvertTo-Json -Compress) }
    if ($args[0] -eq 's3api' -and $args[1] -eq 'head-bucket') { $global:LASTEXITCODE = 1; return }
    if ($args[0] -eq 'cloudformation' -and $args[1] -eq 'describe-stacks') {
        if (!$global:SglMock.Deployed) {
            Write-Error 'An error occurred (ValidationError): Stack with id sgl-sharing does not exist' -ErrorAction Continue
            $global:LASTEXITCODE = 254
            return
        }
        return '[{"OutputKey":"ApiUrl","OutputValue":"https://mock.execute-api.us-west-2.amazonaws.com"},{"OutputKey":"DiscordCallbackUrl","OutputValue":"https://mock.execute-api.us-west-2.amazonaws.com/auth/callback"},{"OutputKey":"BudgetControlFunctionName","OutputValue":"sgl-sharing-budget-guard"}]'
    }
    if ($args[0] -eq 'cloudformation' -and $args[1] -eq 'deploy') { $global:SglMock.Deployed=$true; return '{}' }
    if ($args[0] -eq 'lambda' -and $args[1] -eq 'invoke') {
        $payloadPath=$args[[Array]::IndexOf($args,'--payload')+1].Substring(7)
        $request=Get-Content -LiteralPath $payloadPath -Raw | ConvertFrom-Json
        $destination=$args | Where-Object { $_ -like '*.json.response' } | Select-Object -First 1
        if ($request.action -eq 'pause') { $global:SglMock.Paused=$true }
        if ($request.action -eq 'resume') {
            if ($request.acknowledgeBudgetOverrun -ne $true) { throw 'Resume lost its acknowledgement' }
            $global:SglMock.Paused=$false
        }
        $result=if ($request.action -eq 'reconcile') { @{reconciled=$true} } elseif ($global:SglMock.InvalidStatus) { @{paused='invalid'} } else { @{paused=$global:SglMock.Paused} }
        [IO.File]::WriteAllText($destination,($result | ConvertTo-Json -Compress))
        if ($global:SglMock.FailControl) { return '{"StatusCode":200,"FunctionError":"Unhandled"}' }
        return '{"StatusCode":200}'
    }
    return '{}'
}
function Assert-NoDeployment {
    if ($global:SglDeploymentMockCalls | Where-Object { $_[0] -eq 's3' -or $_[0] -eq 's3api' -or $_[1] -eq 'deploy' }) { throw 'Unsafe deployment continued after a failed guard check.' }
}
try {
    $deploy = Join-Path $infra 'deploy.ps1'
    $control = Join-Path $infra 'budget-control.ps1'
    $options = @{Profile='MOCK_ONLY';AccountId='111111111111';Region='us-west-2';BudgetEmail='ci@example.invalid'}
    try { & $deploy -Profile MOCK_ONLY -AccountId 111111111111 -BudgetEmail invalid -PlanOnly; throw 'Invalid email was accepted.' }
    catch { if ($_.Exception.Message -notlike '*validate argument*BudgetEmail*') { throw } }
    if ($global:SglDeploymentMockCalls.Count) { throw 'Invalid email reached AWS.' }
    & $deploy @options -PlanOnly
    if ($global:SglDeploymentMockCalls.Count -ne 4) { throw 'Plan performed unexpected resource calls.' }
    Assert-NoDeployment
    if (Test-Path -LiteralPath (Join-Path $fixture 'src/SGL.Core/Data/library-service.json')) { throw 'Plan changed app configuration.' }
    $global:SglDeploymentMockCalls.Clear()
    $global:SglMock.Account='222222222222'
    try { & $deploy @options -PlanOnly; throw 'Account mismatch was accepted.' }
    catch { if ($_.Exception.Message -notlike '*different account*') { throw } }
    if ($global:SglDeploymentMockCalls.Count -ne 1) { throw 'Account mismatch did not stop immediately.' }
    $global:SglDeploymentMockCalls.Clear(); $global:SglMock.Account='111111111111'
    $global:SglMock.Concurrency=10
    try { & $deploy @options -UseReservedConcurrency; throw 'Low concurrency quota was accepted for reserved mode.' }
    catch { if ($_.Exception.Message -notlike '*needs at least 103*') { throw } }
    Assert-NoDeployment
    if ($global:SglDeploymentMockCalls | Where-Object { $_[1] -eq 'describe-stacks' }) { throw 'Low quota was not rejected during preflight.' }
    $global:SglDeploymentMockCalls.Clear()
    & $deploy @options -PlanOnly
    Assert-NoDeployment
    $global:SglDeploymentMockCalls.Clear(); $global:SglMock.Concurrency=1001
    & $deploy @options
    foreach ($call in $global:SglDeploymentMockCalls) {
        $profile=[Array]::IndexOf($call,'--profile'); $region=[Array]::IndexOf($call,'--region')
        if ($profile -lt 0 -or $call[$profile+1] -ne 'MOCK_ONLY' -or $region -lt 0 -or $call[$region+1] -ne 'us-west-2') { throw 'Deployment lost its profile/region.' }
    }
    if (!($global:SglDeploymentMockCalls | Where-Object { $_[0] -eq 's3api' -and $_[1] -eq 'create-bucket' -and $_ -contains 'LocationConstraint=us-west-2' })) { throw 'Bucket location is invalid.' }
    if (!($global:SglDeploymentMockCalls | Where-Object { $_[1] -eq 'deploy' -and $_ -contains 'MonthlyBudgetUsd=30' -and $_ -contains 'SharingPaused=false' -and $_ -contains 'UseReservedConcurrency=false' -and $_ -contains 'BudgetEmail=ci@example.invalid' })) { throw 'Deployment lost its budget configuration.' }
    $configuration=Get-Content -LiteralPath (Join-Path $fixture 'src/SGL.Core/Data/library-service.json') -Raw | ConvertFrom-Json
    if ($configuration.apiUrl -ne 'https://mock.execute-api.us-west-2.amazonaws.com') { throw 'API output missing.' }
    $global:SglDeploymentMockCalls.Clear(); $global:SglMock.Paused=$true
    & $deploy @options -MonthlyBudgetUsd 25 -UseReservedConcurrency
    if (!($global:SglDeploymentMockCalls | Where-Object { $_[1] -eq 'deploy' -and $_ -contains 'SharingPaused=true' -and $_ -contains 'MonthlyBudgetUsd=25' })) { throw 'Paused deployment reopened sharing.' }
    if (!$global:SglMock.Paused) { throw 'Deployment resumed sharing.' }
    foreach ($failure in 'FailControl','InvalidStatus') {
        $global:SglDeploymentMockCalls.Clear(); $global:SglMock[$failure]=$true
        try { & $deploy @options; throw 'Invalid guard response was accepted.' }
        catch { if ($_.Exception.Message -notlike 'Budget control*') { throw } }
        Assert-NoDeployment
        $global:SglMock[$failure]=$false
    }
    $global:SglDeploymentMockCalls.Clear()
    try { & $control -Profile MOCK_ONLY -AccountId 111111111111 -Action Resume; throw 'Resume lacked acknowledgement.' }
    catch { if ($_.Exception.Message -notlike 'Resume requires*') { throw } }
    if ($global:SglDeploymentMockCalls.Count) { throw 'Unacknowledged resume invoked AWS.' }
    & $control -Profile MOCK_ONLY -AccountId 111111111111 -Action Status | Out-Null
    if (!$global:SglMock.Paused) { throw 'Status changed sharing.' }
    & $control -Profile MOCK_ONLY -AccountId 111111111111 -Action Resume -AcknowledgeBudgetOverrun | Out-Null
    if ($global:SglMock.Paused) { throw 'Explicit resume did not reach the control.' }
    & $control -Profile MOCK_ONLY -AccountId 111111111111 -Action Pause | Out-Null
    if (!$global:SglMock.Paused) { throw 'Manual pause did not reach the control.' }
    Write-Output 'Passed mocked deployment and operator controls: account guard, mandatory budget config, preserved pause, failed status rejection, status/pause/acknowledged resume. No real AWS calls.'
}
finally {
    $ErrorView = $previousErrorView
    Remove-Item -LiteralPath 'Function:\aws' -ErrorAction SilentlyContinue
    Remove-Variable SglDeploymentMockCalls,SglMock -Scope Global -ErrorAction SilentlyContinue
}
