param(
    [Parameter(Mandatory)][string]$Profile,
    [Parameter(Mandatory)][ValidatePattern('^\d{12}$')][string]$AccountId,
    [string]$Region = 'us-west-2',
    [ValidatePattern('^[a-z][a-z0-9-]{2,40}$')][string]$StackName = 'sgl-sharing',
    [string]$BudgetEmail = '',
    [switch]$PlanOnly
)
$ErrorActionPreference = 'Stop'
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
$work = Join-Path $root 'artifacts/sharing-deploy'
New-Item -ItemType Directory -Force -Path $work | Out-Null
$archive = Join-Path $work 'backend.zip'
Compress-Archive -Path (Join-Path $root 'sharing/backend/*.py') -DestinationPath $archive -Force
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
$bucket = "sgl-deploy-$AccountId-$Region"
$key = "backend/$hash.zip"
if ($PlanOnly) {
    Write-Output "Validated deployment plan: account $AccountId, region $Region, stack $StackName."
    Write-Output 'Resources: private S3, DynamoDB, Lambda, HTTP API, restricted IAM role and Discord secret. No CloudFront.'
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
Invoke-SglAws @('cloudformation','deploy','--template-file',$template,'--stack-name',$StackName,'--capabilities','CAPABILITY_IAM','--parameter-overrides',"CodeBucket=$bucket","CodeKey=$key","BudgetEmail=$BudgetEmail",'--no-fail-on-empty-changeset') | Out-Null
$stack = Invoke-SglAws @('cloudformation','describe-stacks','--stack-name',$StackName,'--query','Stacks[0].Outputs','--output','json') | Out-String | ConvertFrom-Json
$output = @{}
foreach ($entry in $stack) { $output[$entry.OutputKey] = $entry.OutputValue }
$output | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $work 'outputs.json') -Encoding utf8
@{ apiUrl = $output.ApiUrl } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'src/SGL.Core/Data/library-service.json') -Encoding utf8
Write-Output "Deployed SGL sharing: $($output.ApiUrl)"
Write-Output "Add this Discord OAuth redirect: $($output.DiscordCallbackUrl)"
Write-Output 'Next run configure-discord.ps1. Rebuild SGL to include the deployed service URL.'
