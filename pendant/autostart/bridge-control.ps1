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

function Request-BridgeExit {
    # Ask a running bridge to exit cleanly (it finishes its current KFLOP call first)
    # and wait up to 10 s. Returns $true when no bridge is left running.
    if (-not (Get-Process iMachKflop -ErrorAction SilentlyContinue)) { return $true }
    try {
        $evt = [System.Threading.EventWaitHandle]::OpenExisting('Local\iMachKflop.Stop')
        [void]$evt.Set(); $evt.Dispose()
    } catch { Write-Host "Bridge has no stop signal (older build)." }
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Process iMachKflop -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 200
    }
    return -not (Get-Process iMachKflop -ErrorAction SilentlyContinue)
}

function Stop-Bridge {
    # NEVER force-kill a bridge that may be mid-call to the KMotion server: twice
    # (2026-10-01, 2026-10-03) that left the server waiting forever on a dead client,
    # and nothing could reach the KFLOP until a full power cycle. Stopping the
    # scheduled task can also terminate the bridge (it runs inside the task), so:
    #   1. ask the bridge to exit cleanly while its supervisor is still running;
    #   2. stop the supervisor + task during the supervisor's 5 s relaunch delay;
    #   3. if a relaunch slipped through, ask that one to exit too;
    #   4. force only as a last resort, with a warning.
    $clean = Request-BridgeExit
    Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" |
        Where-Object { $_.CommandLine -match 'run-pendant' } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Stop-ScheduledTask -TaskName PendantBridge -ErrorAction SilentlyContinue
    if ($clean) { $clean = Request-BridgeExit }
    if (-not $clean -and (Get-Process iMachKflop -ErrorAction SilentlyContinue)) {
        Write-Host "Bridge did not exit within 10 s -- forcing it. If KMotionCNC / the pendant then can't reach the KFLOP, power-cycle the PC and KFLOP."
        Stop-Process -Name iMachKflop -Force -ErrorAction SilentlyContinue
    }
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