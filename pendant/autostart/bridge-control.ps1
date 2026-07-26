<#
  bridge-control.ps1  --  start / stop / restart the pendant bridge.
    -Action Restart  (default) stop everything, then relaunch supervised (hidden)
    -Action Stop     stop the supervisor and the bridge
    -Action Console  stop everything, then run the bridge in THIS window so you
                     can see the compile line and diagnostics (best for debugging)
  Restart/Console pass -Force, so they work even when Tune.AutoStart is false.
#>
param([ValidateSet('Restart','Stop','Console')][string]$Action = 'Restart')

$wrapper = Join-Path $PSScriptRoot 'run-pendant.ps1'

function Stop-Bridge {
    Stop-ScheduledTask -TaskName PendantBridge -ErrorAction SilentlyContinue
    # kill any supervisor loop, however it was launched (task or icon)
    Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" |
        Where-Object { $_.CommandLine -match 'run-pendant' } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Stop-Process -Name iMachKflop -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
}

switch ($Action) {
    'Stop' {
        Stop-Bridge
        Write-Host "Pendant bridge stopped."
    }
    'Restart' {
        Stop-Bridge
        Start-Process powershell.exe -WindowStyle Hidden -ArgumentList `
            '-NoProfile','-ExecutionPolicy','Bypass','-File',"`"$wrapper`"",'-Force'
        Write-Host "Pendant bridge restarted (supervised, hidden)."
    }
    'Console' {
        Stop-Bridge
        $exe = Get-ChildItem "C:\KMotion*\KMotion\Release64\iMachKflop.exe" |
               Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if (-not $exe) { Write-Host "iMachKflop.exe not found."; return }
        Write-Host "Running $($exe.FullName)`n"
        & $exe.FullName
    }
}