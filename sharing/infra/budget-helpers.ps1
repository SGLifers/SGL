function Get-SglStackOutputs {
    param([string]$Profile,[string]$Region,[string]$StackName,[switch]$AllowMissing)
    $diagnostic = Join-Path ([IO.Path]::GetTempPath()) ('sgl-stack-' + [guid]::NewGuid().ToString('N') + '.txt')
    try {
        $json = & aws cloudformation describe-stacks --stack-name $StackName --profile $Profile --region $Region --query 'Stacks[0].Outputs' --output json --no-cli-pager 2>$diagnostic
        if ($LASTEXITCODE -ne 0) {
            $message = Get-Content -LiteralPath $diagnostic -Raw
            if ($AllowMissing -and $message -like '*ValidationError*' -and $message -like '*does not exist*') { return @{} }
            throw 'Could not inspect the sharing stack. Deployment stopped rather than assuming sharing is enabled.'
        }
        $outputs = @{}
        foreach ($entry in ($json | Out-String | ConvertFrom-Json)) { $outputs[$entry.OutputKey] = $entry.OutputValue }
        return $outputs
    }
    finally { if (Test-Path -LiteralPath $diagnostic) { Remove-Item -LiteralPath $diagnostic -Force } }
}

function Invoke-SglBudgetControl {
    param([string]$Profile,[string]$Region,[string]$FunctionName,[ValidateSet('status','pause','resume','reconcile')][string]$Action,[switch]$AcknowledgeBudgetOverrun)
    $request = Join-Path ([IO.Path]::GetTempPath()) ('sgl-budget-' + [guid]::NewGuid().ToString('N') + '.json')
    $response = $request + '.response'
    try {
        $body = @{ action = $Action }
        if ($AcknowledgeBudgetOverrun) { $body.acknowledgeBudgetOverrun = $true }
        [IO.File]::WriteAllText($request, ($body | ConvertTo-Json -Compress), [Text.UTF8Encoding]::new($false))
        $metadata = & aws lambda invoke --function-name $FunctionName --invocation-type RequestResponse --payload "file://$request" --cli-binary-format raw-in-base64-out $response --profile $Profile --region $Region --output json --no-cli-pager
        if ($LASTEXITCODE -ne 0) { throw 'Could not invoke the budget control. No successful state change was confirmed.' }
        $metadata = $metadata | Out-String | ConvertFrom-Json
        if ($metadata.FunctionError -or $metadata.StatusCode -ne 200) { throw 'Budget control reported a failure. Inspect its CloudWatch logs and alarms; retry pause if protection is uncertain.' }
        $state = Get-Content -LiteralPath $response -Raw | ConvertFrom-Json
        if (($Action -eq 'reconcile' -and $state.reconciled -ne $true) -or ($Action -ne 'reconcile' -and $state.paused -isnot [bool])) { throw 'Budget control returned an invalid status. No successful state change was confirmed.' }
        return $state
    }
    finally {
        foreach ($path in $request,$response) { if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force } }
    }
}
