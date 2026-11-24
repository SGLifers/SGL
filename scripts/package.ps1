param([string]$Version='0.4.0',[string]$OutputDirectory='dist',[switch]$UnsignedMetadata)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Use a numeric MAJOR.MINOR.PATCH version.' }
$output=[IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
New-Item -ItemType Directory -Force -Path $output | Out-Null
$publish=Join-Path $output 'package'
& dotnet publish (Join-Path $root 'src/SGL.Desktop/SGL.Desktop.csproj') -c Release -r win-x64 --self-contained true -p:Version=$Version -o $publish --nologo
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
foreach($name in @('README.md','LICENSE','THIRD-PARTY-NOTICES.md')) { Copy-Item -LiteralPath (Join-Path $root $name) -Destination $publish -Force }
$guide=Join-Path $publish 'Guide'
New-Item -ItemType Directory -Force -Path $guide | Out-Null
Copy-Item -Path (Join-Path $root 'docs/*') -Destination $guide -Recurse -Force
$thirdParty=Join-Path $publish 'ThirdParty'
New-Item -ItemType Directory -Force -Path $thirdParty | Out-Null
$licenses=@{
 'LibGit2Sharp.txt'='https://raw.githubusercontent.com/libgit2/libgit2sharp/master/LICENSE.md'
 'libgit2.txt'='https://raw.githubusercontent.com/libgit2/libgit2/main/COPYING'
 '.NET.txt'='https://raw.githubusercontent.com/dotnet/runtime/main/LICENSE.TXT'
 '.NET-ThirdParty.txt'='https://raw.githubusercontent.com/dotnet/runtime/main/THIRD-PARTY-NOTICES.TXT'
 'WindowsDesktop.txt'='https://raw.githubusercontent.com/dotnet/wpf/main/LICENSE.TXT'
}
foreach($name in $licenses.Keys){ Invoke-WebRequest -Uri $licenses[$name] -OutFile (Join-Path $thirdParty $name) }
Copy-Item -LiteralPath (Join-Path $publish 'SGL.exe') -Destination (Join-Path $output 'SGL.exe') -Force
# The ZIP is the full manual installation. The EXE is the verified update asset.
$zipInputs=@('SGL.exe','README.md','LICENSE','THIRD-PARTY-NOTICES.md','Guide','ThirdParty') | ForEach-Object {Join-Path $publish $_}
Compress-Archive -Path $zipInputs -DestinationPath (Join-Path $output 'SGL.zip') -Force
if(-not $UnsignedMetadata) {
 & dotnet run --project (Join-Path $root 'tools/SGL.Release') -- sign (Join-Path $output 'SGL.exe') $Version (Join-Path $output 'update.json')
 if($LASTEXITCODE -ne 0){throw 'Release metadata signing failed.'}
}
$hashes=@('SGL.exe','SGL.zip','update.json') | ForEach-Object { $path=Join-Path $output $_;if(Test-Path -LiteralPath $path){'{0}  {1}' -f (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash,$_} }
$hashes | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding ascii
Write-Output "Packaged SGL $Version in $output"
