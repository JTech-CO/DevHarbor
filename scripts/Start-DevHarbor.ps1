[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$repoPath=Split-Path -Parent $PSScriptRoot
$localSdk=Join-Path $repoPath '.tools/dotnet/dotnet.exe'
$sdk=if(Test-Path -LiteralPath $localSdk){$localSdk}else{(Get-Command dotnet -ErrorAction Stop).Source}
Push-Location $repoPath
try {
    $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE='false'
    $env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH='false'
    $env:DOTNET_CLI_HOME=Join-Path $repoPath '.tools/cli-home'
    & $sdk run --project src/DevHarbor.Desktop --configuration Release
    if($LASTEXITCODE -ne 0){throw 'DevHarbor startup failed'}
} finally { Pop-Location }
