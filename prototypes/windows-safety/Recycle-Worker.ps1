[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('Recycle','Restore')][string]$Mode,[Parameter(Mandatory)][string]$RunDirectory)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
Add-Type -Path (Join-Path $PSScriptRoot 'SafetyCore.cs')
Add-Type -Path (Join-Path $PSScriptRoot 'ShellRecycle.cs')
$runs=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'runs'))
$run=[IO.Path]::GetFullPath($RunDirectory)
if(-not $run.StartsWith($runs+'\',[StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $run -PathType Container)) { throw 'Run must exist under prototype runs directory' }
$manifestPath=Join-Path $run 'recycle-manifest.json'
if($Mode -eq 'Recycle') {
    if(Test-Path -LiteralPath $manifestPath) { throw 'Refusing to overwrite an existing manifest' }
    $path=Join-Path $run ('devharbor-fixture-'+[guid]::NewGuid().ToString('N')+'.txt')
    [IO.File]::WriteAllText($path,"DEVHARBOR SYNTHETIC FIXTURE`n"+[guid]::NewGuid())
    $lease=[DevHarbor.Probe.BoundaryLease]::Open($run,$path);$lease.Dispose()
    $record=[ordered]@{original=$path;sha256=(Get-FileHash -LiteralPath $path).Hash;state='IntentRecorded';recycled=$null;restorePath=$null;workerPid=$PID;vetoVerified=$false;collisionVerified=$false}
    $record|ConvertTo-Json|Set-Content -LiteralPath $manifestPath -Encoding utf8
    $rejected=$false
    try { [DevHarbor.Probe.RecycleProbe]::RecycleSynthetic($path,$true)|Out-Null } catch { $rejected=$true }
    if(-not $rejected -or -not (Test-Path -LiteralPath $path)) { throw 'Pre-delete veto failed' }
    $record.vetoVerified=$true
    $record.recycled=[DevHarbor.Probe.RecycleProbe]::RecycleSynthetic($path,$false)
    $record.state='Recycled'
    $record|ConvertTo-Json|Set-Content -LiteralPath $manifestPath -Encoding utf8
    if(Test-Path -LiteralPath $path) { throw 'Original still exists' }
} else {
    $record=Get-Content -LiteralPath $manifestPath -Raw|ConvertFrom-Json
    if($record.state -ne 'Recycled' -or $record.workerPid -eq $PID) { throw 'Expected recycled manifest from another process' }
    if([IO.Path]::GetDirectoryName($record.original) -ne $run) { throw 'Manifest original outside fixture directory' }
    if((Get-FileHash -LiteralPath $record.recycled).Hash -ne $record.sha256) { throw 'Recycled hash mismatch' }
    # Leave a new file at the original path to prove restore cannot overwrite it.
    [IO.File]::WriteAllText($record.original,'NEW SYNTHETIC FILE MUST SURVIVE')
    $rejected=$false
    try { [DevHarbor.Probe.RecycleProbe]::RestoreSynthetic($record.recycled,$record.original) } catch { $rejected=$true }
    if(-not $rejected -or [IO.File]::ReadAllText($record.original) -ne 'NEW SYNTHETIC FILE MUST SURVIVE') { throw 'Collision guard failed' }
    $record.collisionVerified=$true
    $record.restorePath=$record.original+'.restored.txt'
    [DevHarbor.Probe.RecycleProbe]::RestoreSynthetic($record.recycled,$record.restorePath)
    if((Get-FileHash -LiteralPath $record.restorePath).Hash -ne $record.sha256) { throw 'Restored content mismatch' }
    if(Test-Path -LiteralPath $record.recycled) { throw 'Recycled payload still present after restore' }
    $record.state='Restored'
    $record|ConvertTo-Json|Set-Content -LiteralPath $manifestPath -Encoding utf8
}
