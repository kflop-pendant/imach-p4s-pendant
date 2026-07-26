<#
  install-pendant-task.ps1
  Registers the "PendantBridge" scheduled task: at logon, run run-pendant.ps1
  (hidden window), which keeps the pendant bridge alive and relaunches it on
  exit. Also exports the task definition to PendantBridge.task.xml (next to this
  script) so the setup is version-controlled in the repo.

  Run ONCE. If it fails with access-denied, run it from an elevated PowerShell
  (Run as administrator). The TASK itself still runs non-elevated, as you.
#>

$TaskName = "PendantBridge"
$wrapper  = Join-Path $PSScriptRoot "run-pendant.ps1"

if (-not (Test-Path -LiteralPath $wrapper)) {
    Write-Error "run-pendant.ps1 not found next to this script ($wrapper)."
    return
}

$action = New-ScheduledTaskAction -Execute "powershell.exe" `
    -Argument ("-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"{0}`"" -f $wrapper)

$trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME

$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME `
    -LogonType Interactive -RunLevel Limited

$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -MultipleInstances IgnoreNew `
    -StartWhenAvailable

Register-ScheduledTask -TaskName $TaskName -Force `
    -Action $action -Trigger $trigger -Principal $principal -Settings $settings `
    -Description "Auto-start and keep the iMach P4-S pendant bridge running at logon (relaunches on exit)." | Out-Null

$xml = Join-Path $PSScriptRoot "PendantBridge.task.xml"
Export-ScheduledTask -TaskName $TaskName | Out-File -FilePath $xml -Encoding utf8

Write-Output "Registered '$TaskName'."
Write-Output "Exported definition to $xml"
Write-Output "Test now with:  Start-ScheduledTask -TaskName $TaskName"
