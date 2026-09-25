# Manual, non-destructive UI demo. Never invokes ShellRecycle or a cleanup command.
$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationFramework
Add-Type -Path (Join-Path $PSScriptRoot 'SafetyCore.cs')
$demoRoot=Join-Path $PSScriptRoot ('runs/ui-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $demoRoot | Out-Null
$demoFile=Join-Path $demoRoot 'approval-preview.txt'
[IO.File]::WriteAllText($demoFile,'Non-destructive approval demo')
$plan=[DevHarbor.Probe.ApprovalProbe]::new($demoRoot,$demoFile,[DateTime]::UtcNow)
$window=[Windows.Window]::new()
$window.Title='DevHarbor · 승인 흐름 실험'
$window.Width=700; $window.Height=420; $window.WindowStartupLocation='CenterScreen'
$panel=[Windows.Controls.StackPanel]::new();$panel.Margin='24';$window.Content=$panel
$text=[Windows.Controls.TextBlock]::new();$text.TextWrapping='Wrap'
$text.Text="계획 검토 — 파일을 삭제하지 않는 실험`n`n대상: $demoFile`n`n동작: 파일 경계를 다시 검사하고 완료 상태만 기록`n`n계획: $($plan.Digest)`n유효기간: 생성 후 3분`n`n승인 없이 실행되지 않으며 이 창은 실제 정리 기능과 연결되지 않습니다."
$panel.Children.Add($text)|Out-Null
$approve=[Windows.Controls.Button]::new();$approve.Content='이 계획의 검증 실행 승인';$approve.Margin='0,20,0,8';$approve.Height=36
$deny=[Windows.Controls.Button]::new();$deny.Content='취소';$deny.Height=36;$deny.IsCancel=$true
$panel.Children.Add($approve)|Out-Null;$panel.Children.Add($deny)|Out-Null
$approve.Add_Click({
    try {
        $plan.DecideFromTestUI($plan.Digest,$true,[DateTime]::UtcNow)
        $plan.ExecuteProbe($plan.Digest,[DateTime]::UtcNow,[Action]{})
        [Windows.MessageBox]::Show('검증만 완료했습니다. 파일은 그대로 있습니다.','DevHarbor')|Out-Null
    } catch { [Windows.MessageBox]::Show($_.Exception.Message,'실행 거부')|Out-Null }
    $window.Close()
})
$deny.Add_Click({$window.Close()})
$window.Add_Closing({if($plan.State -eq 'AwaitingApproval'){try{$plan.DecideFromTestUI($plan.Digest,$false,[DateTime]::UtcNow)}catch{}}})
$window.ShowDialog()|Out-Null
Write-Output ('Final state: '+$plan.State)
