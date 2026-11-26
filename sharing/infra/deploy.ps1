param(
    [Parameter(Mandatory)][string]$Profile,
    [Parameter(Mandatory)][ValidatePattern('^\d{12}$')][string]$AccountId,
    [string]$Region = 'us-west-2',
    [ValidatePattern('^[a-z][a-z0-9-]{2,40}$')][string]$StackName = 'sgl-sharing',
    [Parameter(Mandatory)][ValidatePattern('^[^\s@]+@[^\s@]+\.[^\s@]+$')][string]$BudgetEmail,
    [ValidateRange(1,10000)][decimal]$MonthlyBudgetUsd = 30,
    [switch]$UseReservedConcurrency,
    [switch]$PlanOnly
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'budget-helpers.ps1')
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
function Invoke-SglAws {
    param([Parameter(ValueFromRemainingArguments=$true)][string[]]$Arguments)
    $result = & aws @Arguments --profile $Profile --region $Region --no-cli-pager
    if ($LASTEXITCODE -ne 0) { throw "AWS command failed: $($Arguments[0..1] -join ' ')" }
    return $result
}
$identity = (Invoke-SglAws @('sts','get-caller-identity','--output','json') | Out-String | ConvertFrom-Json)
if ($identity.Account -ne $AccountId) { throw "The selected profile is signed into a different account. No resources were changed." }
$template = Join-Path $PSScriptRoot 'template.json'
Invoke-SglAws @('cloudformation','validate-template','--template-body',"file://$template") | Out-Null
$limits = Invoke-SglAws @('lambda','get-account-settings','--query','AccountLimit','--output','json') | Out-String | ConvertFrom-Json
if ($UseReservedConcurrency -and $limits.ConcurrentExecutions -lt 103) { throw "Lambda's regional concurrency quota is $($limits.ConcurrentExecutions). Reserved mode needs at least 103 (3 reserved plus AWS's required 100 unreserved). Request a quota increase or omit -UseReservedConcurrency. No resource configuration was changed." }
$reservedMode = $UseReservedConcurrency.IsPresent.ToString().ToLowerInvariant()
$existing = Get-SglStackOutputs -Profile $Profile -Region $Region -StackName $StackName -AllowMissing
$sharingPaused = 'false'
if ($existing.BudgetControlFunctionName) {
    $state = Invoke-SglBudgetControl -Profile $Profile -Region $Region -FunctionName $existing.BudgetControlFunctionName -Action status
    if ($state.paused) { $sharingPaused = 'true' }
}
$work = Join-Path $root 'artifacts/sharing-deploy'
New-Item -ItemType Directory -Force -Path $work | Out-Null
$archive = Join-Path $work 'backend.zip'
Compress-Archive -Path (Join-Path $root 'sharing/backend/*.py') -DestinationPath $archive -Force
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
$bucket = "sgl-deploy-$AccountId-$Region"
$key = "backend/$hash.zip"
if ($PlanOnly) {
    Write-Output "Validated deployment plan: account $AccountId, region $Region, stack $StackName."
    Write-Output "Account-wide monthly budget: USD $MonthlyBudgetUsd. Alerts: 50%, 80%, 100%; automatic sharing cutoff at 100%. Preserved pause: $sharingPaused."
    Write-Output "Reserved concurrency enabled: $reservedMode."
    Write-Output 'Resources include budget SNS, cutoff Lambda, reconciliation, failure alarms and a failure queue. No CloudFront.'
    Write-Output 'No AWS resources were changed. Run without -PlanOnly to deploy.'
    return
}
& aws s3api head-bucket --bucket $bucket --profile $Profile --region $Region --no-cli-pager 2>$null
if ($LASTEXITCODE -ne 0) {
    $create = @('s3api','create-bucket','--bucket',$bucket)
    if ($Region -ne 'us-east-1') { $create += @('--create-bucket-configuration',"LocationConstraint=$Region") }
    Invoke-SglAws $create | Out-Null
}
Invoke-SglAws @('s3api','put-public-access-block','--bucket',$bucket,'--public-access-block-configuration','BlockPublicAcls=true,IgnorePublicAcls=true,BlockPublicPolicy=true,RestrictPublicBuckets=true') | Out-Null
Invoke-SglAws @('s3','cp',$archive,"s3://$bucket/$key",'--only-show-errors') | Out-Null
$amount = $MonthlyBudgetUsd.ToString([Globalization.CultureInfo]::InvariantCulture)
Invoke-SglAws @('cloudformation','deploy','--template-file',$template,'--stack-name',$StackName,'--capabilities','CAPABILITY_IAM','--parameter-overrides',"CodeBucket=$bucket","CodeKey=$key","BudgetEmail=$BudgetEmail","MonthlyBudgetUsd=$amount","SharingPaused=$sharingPaused","UseReservedConcurrency=$reservedMode",'--no-fail-on-empty-changeset') | Out-Null
$output = Get-SglStackOutputs -Profile $Profile -Region $Region -StackName $StackName
$output | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $work 'outputs.json') -Encoding utf8
if (!$output.BudgetControlFunctionName) { throw 'Deployment returned no budget guard. Review the stack before enabling sharing.' }
Invoke-SglBudgetControl -Profile $Profile -Region $Region -FunctionName $output.BudgetControlFunctionName -Action reconcile | Out-Null
@{ apiUrl = $output.ApiUrl } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'src/SGL.Core/Data/library-service.json') -Encoding utf8
Write-Output "Deployed SGL sharing: $($output.ApiUrl)"
Write-Output "Add this Discord OAuth redirect: $($output.DiscordCallbackUrl)"
Write-Output 'Next run configure-discord.ps1. Rebuild SGL to include the deployed service URL.'
Write-Output 'Confirm the monitoring SNS email subscription, then test pause/status/resume before community rollout. Existing pauses are never cleared by deployment.'
