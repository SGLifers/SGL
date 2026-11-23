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
    $value = $credential.Password.Trim()
    if ([string]::IsNullOrWhiteSpace($value) -or $value -match '^[*\u2022\u25CF]+$') { throw 'Copy the actual OAuth2 client secret, rather than masked placeholder characters.' }
    $callback = & aws cloudformation describe-stacks --stack-name $StackName --profile $Profile --region $Region --query "Stacks[0].Outputs[?OutputKey=='DiscordCallbackUrl'].OutputValue | [0]" --output text --no-cli-pager
    if ($LASTEXITCODE -ne 0 -or $callback -notmatch '^https://') { throw 'Could not find the deployed callback URL.' }
    # A deliberately invalid code cannot create a token. invalid_grant confirms
    # application authentication succeeded, before the new secret is persisted.
    Add-Type -AssemblyName System.Net.Http
    $handler = [System.Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $false
    $client = [System.Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromSeconds(15)
    $client.DefaultRequestHeaders.UserAgent.ParseAdd('DiscordBot (https://github.com/SGLifers/SGL, 0.3.1)')
    $fields = [Collections.Generic.Dictionary[string,string]]::new()
    $fields.Add('client_id',$ClientId)
    $fields.Add('client_secret',$value)
    $fields.Add('grant_type','authorization_code')
    $fields.Add('code',('SGL_INVALID_CONFIG_CHECK_' + [guid]::NewGuid().ToString('N')))
    $fields.Add('redirect_uri',$callback.Trim())
    $form = [System.Net.Http.FormUrlEncodedContent]::new($fields)
    try {
        $reply = $client.PostAsync('https://discord.com/api/oauth2/token',$form).GetAwaiter().GetResult()
        $response = $reply.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
        if ([int]$reply.StatusCode -eq 401 -or $response.error -eq 'invalid_client') { throw 'Discord rejected this client ID/secret pair. Copy the current Client Secret from this same application under OAuth2. Nothing was stored.' }
        if ([int]$reply.StatusCode -ne 400 -or $response.error -ne 'invalid_grant') { throw 'Discord did not confirm the application credentials. Nothing was stored; check the application and callback settings.' }
    }
    finally { $form.Dispose(); $client.Dispose(); $handler.Dispose() }
    # Create the file with access limited to the current Windows account before writing.
    $stream = [IO.File]::Open($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    $stream.Dispose()
    $acl = Get-Acl -LiteralPath $temporary
    $acl.SetAccessRuleProtection($true, $false)
    $sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
    $rule = [System.Security.AccessControl.FileSystemAccessRule]::new($sid, 'FullControl', 'Allow')
    $acl.SetAccessRule($rule)
    Set-Acl -LiteralPath $temporary -AclObject $acl
    $json = @{ clientId = $ClientId; clientSecret = $value } | ConvertTo-Json -Compress
    [IO.File]::WriteAllText($temporary, $json, [Text.UTF8Encoding]::new($false))
    & aws secretsmanager put-secret-value --secret-id $secretArn --secret-string "file://$temporary" --profile $Profile --region $Region --query ARN --output text --no-cli-pager | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not store Discord configuration.' }
    Write-Output 'Discord credentials verified and stored. Sign-in becomes available within 60 seconds.'
}
finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    $secret.Dispose()
}
