[CmdletBinding()]
param([switch]$IncludeRecycle)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -Path (Join-Path $PSScriptRoot 'SafetyCore.cs')
$probeBase = Join-Path $PSScriptRoot ('runs/' + [guid]::NewGuid().ToString('N'))
$allowed = Join-Path $probeBase 'allowed'
$outside = Join-Path $probeBase 'allowed-sibling'
New-Item -ItemType Directory -Path $allowed, $outside | Out-Null
$results = [Collections.Generic.List[object]]::new()
function Check([string]$name, [scriptblock]$body) {
    try { & $body; $results.Add([pscustomobject]@{name=$name; status='passed'; detail=''}) }
    catch { $results.Add([pscustomobject]@{name=$name; status='failed'; detail=$_.Exception.Message}) }
}
function Expect-Reject([scriptblock]$body) {
    $rejected=$false
    try { & $body } catch { $rejected=$true }
    if(-not $rejected) { throw 'Expected rejection but operation succeeded' }
}
function New-Fixture {
    $p=Join-Path $allowed ('devharbor-fixture-' + [guid]::NewGuid().ToString('N') + '.txt')
    [IO.File]::WriteAllText($p,"DEVHARBOR SYNTHETIC FIXTURE`n" + [guid]::NewGuid())
    return $p
}
$sample=New-Fixture
Check 'boundary.unicode-and-spaces' {
    $dir=Join-Path $allowed '한글 폴더'; New-Item -ItemType Directory -Path $dir | Out-Null
    $file=Join-Path $dir 'sample.txt'; [IO.File]::WriteAllText($file,'fixture')
    $lease=[DevHarbor.Probe.BoundaryLease]::Open($allowed,$file); $lease.Dispose()
}
Check 'boundary.root-itself-rejected' { Expect-Reject { [DevHarbor.Probe.BoundaryLease]::Open($allowed,$allowed) } }
Check 'boundary.prefix-sibling-rejected' {
    $p=Join-Path $outside 'outside.txt'; [IO.File]::WriteAllText($p,'outside fixture')
    Expect-Reject { [DevHarbor.Probe.BoundaryLease]::Open($allowed,$p) }
}
Check 'boundary.traversal-rejected' { Expect-Reject { [DevHarbor.Probe.BoundaryLease]::Open($allowed,($allowed+'\..\allowed-sibling\outside.txt')) } }
Check 'boundary.ads-rejected' { Expect-Reject { [DevHarbor.Probe.BoundaryLease]::Open($allowed,($sample+':stream')) } }
Check 'boundary.unc-rejected' { Expect-Reject { [DevHarbor.Probe.BoundaryLease]::Open($allowed,'\\localhost\C$\fake') } }
Check 'boundary.missing-rejected' { Expect-Reject { [DevHarbor.Probe.BoundaryLease]::Open($allowed,(Join-Path $allowed 'missing')) } }
Check 'boundary.directory-rejected' { Expect-Reject { [DevHarbor.Probe.BoundaryLease]::Open($allowed,(Join-Path $allowed '한글 폴더')) } }
Check 'boundary.junction-escape-rejected' {
    $link=Join-Path $allowed 'junction'
    New-Item -ItemType Junction -Path $link -Target $outside | Out-Null
    Expect-Reject { [DevHarbor.Probe.BoundaryLease]::Open($allowed,(Join-Path $link 'outside.txt')) }
}
Check 'boundary.hardlink-rejected' {
    $p=New-Fixture; $link=Join-Path $allowed 'hardlink.txt'
    New-Item -ItemType HardLink -Path $link -Target $p | Out-Null
    Expect-Reject { [DevHarbor.Probe.BoundaryLease]::Open($allowed,$p) }
}
Check 'boundary.locked-file-rejected' {
    $handle=[IO.File]::Open($sample,'Open','ReadWrite','None')
    try { Expect-Reject { [DevHarbor.Probe.BoundaryLease]::Open($allowed,$sample) } } finally { $handle.Dispose() }
}
Check 'boundary.lease-blocks-write' {
    $lease=[DevHarbor.Probe.BoundaryLease]::Open($allowed,$sample)
    try { Expect-Reject { [IO.File]::WriteAllText($sample,'unexpected mutation') } } finally { $lease.Dispose() }
}
Check 'boundary.lease-blocks-parent-rename' {
    $lease=[DevHarbor.Probe.BoundaryLease]::Open($allowed,$sample)
    try { Expect-Reject { [IO.Directory]::Move($allowed,($allowed+'-moved')) } } finally { $lease.Dispose() }
}
$now=[DateTime]::UtcNow
Check 'approval.without-consent-rejected' {
    $plan=[DevHarbor.Probe.ApprovalProbe]::new($allowed,$sample,$now)
    Expect-Reject { $plan.ExecuteProbe($plan.Digest,$now,[Action]{}) }
}
Check 'approval.denial-rejected' {
    $plan=[DevHarbor.Probe.ApprovalProbe]::new($allowed,$sample,$now)
    $plan.DecideFromTestUI($plan.Digest,$false,$now)
    Expect-Reject { $plan.ExecuteProbe($plan.Digest,$now,[Action]{}) }
}
Check 'approval.plan-tampering-rejected' {
    $plan=[DevHarbor.Probe.ApprovalProbe]::new($allowed,$sample,$now)
    Expect-Reject { $plan.DecideFromTestUI('changed-plan',$true,$now) }
}
Check 'approval.expiry-rejected' {
    $plan=[DevHarbor.Probe.ApprovalProbe]::new($allowed,$sample,$now)
    $plan.DecideFromTestUI($plan.Digest,$true,$now)
    Expect-Reject { $plan.ExecuteProbe($plan.Digest,$now.AddMinutes(4),[Action]{}) }
}
Check 'approval.target-mutation-rejected' {
    $p=New-Fixture; $plan=[DevHarbor.Probe.ApprovalProbe]::new($allowed,$p,$now)
    $plan.DecideFromTestUI($plan.Digest,$true,$now); [IO.File]::AppendAllText($p,'changed')
    Expect-Reject { $plan.ExecuteProbe($plan.Digest,$now,[Action]{}) }
}
Check 'approval.file-replacement-rejected' {
    $p=New-Fixture; $plan=[DevHarbor.Probe.ApprovalProbe]::new($allowed,$p,$now)
    $plan.DecideFromTestUI($plan.Digest,$true,$now)
    # Both paths are synthetic files directly under this run's allowed root.
    [IO.File]::Move($p,($p+'.original')); [IO.File]::WriteAllText($p,'replacement')
    Expect-Reject { $plan.ExecuteProbe($plan.Digest,$now,[Action]{}) }
}
Check 'approval.execute-once-and-replay-rejected' {
    $plan=[DevHarbor.Probe.ApprovalProbe]::new($allowed,$sample,$now)
    $plan.DecideFromTestUI($plan.Digest,$true,$now)
    $plan.ExecuteProbe($plan.Digest,$now,[Action]{})
    if($plan.State -ne 'Completed') { throw 'Did not complete' }
    Expect-Reject { $plan.ExecuteProbe($plan.Digest,$now,[Action]{}) }
}
Check 'approval.executor-failure-consumes-approval' {
    $plan=[DevHarbor.Probe.ApprovalProbe]::new($allowed,$sample,$now)
    $plan.DecideFromTestUI($plan.Digest,$true,$now)
    Expect-Reject { $plan.ExecuteProbe($plan.Digest,$now,[Action]{ throw 'simulated executor failure' }) }
    if($plan.State -ne 'Failed') { throw 'Failure not recorded' }
    Expect-Reject { $plan.ExecuteProbe($plan.Digest,$now,[Action]{}) }
}
if($IncludeRecycle) {
    Check 'recycle.separate-process-roundtrip-and-collision' {
        $exe=(Get-Process -Id $PID).Path
        & $exe -NoProfile -STA -File (Join-Path $PSScriptRoot 'Recycle-Worker.ps1') -Mode Recycle -RunDirectory $probeBase
        if($LASTEXITCODE -ne 0) { throw 'Recycle worker failed' }
        & $exe -NoProfile -STA -File (Join-Path $PSScriptRoot 'Recycle-Worker.ps1') -Mode Restore -RunDirectory $probeBase
        if($LASTEXITCODE -ne 0) { throw 'Fresh-process restore worker failed' }
    }
} else { $results.Add([pscustomobject]@{name='recycle.separate-process-roundtrip-and-collision';status='not-run';detail='Pass -IncludeRecycle to use synthetic fixture only'}) }
$report=[ordered]@{timeUtc=[DateTime]::UtcNow.ToString('o');os=[Environment]::OSVersion.VersionString;powerShell=$PSVersionTable.PSVersion.ToString();runDirectory=$probeBase;productionDeletionEnabled=$false;humanApprovalUiVerified=$false;results=$results.ToArray()}
$json=$report|ConvertTo-Json -Depth 6
$json|Set-Content -LiteralPath (Join-Path $probeBase 'results.json') -Encoding utf8
$json|Set-Content -LiteralPath (Join-Path $PSScriptRoot 'latest-results.json') -Encoding utf8
$results|Format-Table name,status,detail -AutoSize
Write-Output "Report: $probeBase\results.json"
if(@($results|Where-Object status -eq 'failed').Count -gt 0) { exit 1 }
