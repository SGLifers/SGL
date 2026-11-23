param(
    [Parameter(Mandatory)][string]$Profile,
    [string]$Region = 'us-west-2',
    [string]$StackName = 'sgl-sharing',
    [Parameter(Mandatory)][ValidatePattern('^\d{5,24}$')][string]$ClientId
)
$ErrorActionPreference = 'Stop'
$secretArn = & aws cloudformation describe-stacks --stack-name $StackName --profile $Profile --region $Region --query "Stacks[0].Outputs[?OutputKey=='DiscordSecretArn'].OutputValue | [0]" --output text --no-cli-pager
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($secretArn)) { throw 'Could not find the deployed Discord secret.' }
$secret = Read-Host 'Discord client secret (stored in AWS Secrets Manager, never printed)' -AsSecureString
$credential = [System.Net.NetworkCredential]::new('', $secret)
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('sgl-discord-' + [guid]::NewGuid().ToString('N') + '.json')
try {
    # Create the file with access limited to the current Windows account before writing.
    $stream = [IO.File]::Open($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    $stream.Dispose()
    $acl = Get-Acl -LiteralPath $temporary
    $acl.SetAccessRuleProtection($true, $false)
    $sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
    $rule = [System.Security.AccessControl.FileSystemAccessRule]::new($sid, 'FullControl', 'Allow')
    $acl.SetAccessRule($rule)
    Set-Acl -LiteralPath $temporary -AclObject $acl
    $json = @{ clientId = $ClientId; clientSecret = $credential.Password } | ConvertTo-Json -Compress
    [IO.File]::WriteAllText($temporary, $json, [Text.UTF8Encoding]::new($false))
    & aws secretsmanager put-secret-value --secret-id $secretArn --secret-string "file://$temporary" --profile $Profile --region $Region --query ARN --output text --no-cli-pager | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not store Discord configuration.' }
    Write-Output 'Discord configuration stored. Sign-in becomes available within 60 seconds.'
}
finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    $secret.Dispose()
}
