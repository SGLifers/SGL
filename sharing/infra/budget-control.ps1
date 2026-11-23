param(
    [Parameter(Mandatory)][string]$Profile,
    [Parameter(Mandatory)][ValidatePattern('^\d{12}$')][string]$AccountId,
    [string]$Region = 'us-west-2',
    [ValidatePattern('^[a-z][a-z0-9-]{2,40}$')][string]$StackName = 'sgl-sharing',
    [ValidateSet('Status','Pause','Resume')][string]$Action = 'Status',
    [switch]$AcknowledgeBudgetOverrun
)
$ErrorActionPreference = 'Stop'
if ($Action -eq 'Resume' -and !$AcknowledgeBudgetOverrun) { throw 'Resume requires -AcknowledgeBudgetOverrun. Billing can be delayed and additional spending may occur.' }
. (Join-Path $PSScriptRoot 'budget-helpers.ps1')
$identity = & aws sts get-caller-identity --profile $Profile --region $Region --output json --no-cli-pager
if ($LASTEXITCODE -ne 0) { throw 'Could not verify the AWS account.' }
if (($identity | Out-String | ConvertFrom-Json).Account -ne $AccountId) { throw 'The profile is signed into a different account. No sharing controls were changed.' }
$outputs = Get-SglStackOutputs -Profile $Profile -Region $Region -StackName $StackName
if (!$outputs.BudgetControlFunctionName) { throw 'This stack has no budget guard. Deploy the updated infrastructure first.' }
$state = Invoke-SglBudgetControl -Profile $Profile -Region $Region -FunctionName $outputs.BudgetControlFunctionName -Action $Action.ToLowerInvariant() -AcknowledgeBudgetOverrun:$AcknowledgeBudgetOverrun
$state | ConvertTo-Json -Depth 5
