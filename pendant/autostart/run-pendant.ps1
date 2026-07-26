<#
  run-pendant.ps1
  Keeps the iMach P4-S -> KFLOP pendant bridge running.

  Launches iMachKflop.exe and waits. When it exits (e.g. after a KFLOP
  power-cycle -- which the bridge now handles by failing safe and exiting),
  waits a few seconds and relaunches. Installed as the "PendantBridge" Task
  Scheduler task that runs at logon (see install-pendant-task.ps1).

  The exe path is auto-detected as the newest-built iMachKflop.exe under any
  C:\KMotion*\KMotion\Release64, so a KMotion upgrade needs no change here.

  To stop it (e.g. during development):
     Stop-ScheduledTask -TaskName PendantBridge
     Stop-Process -Name iMachKflop -Force -ErrorAction SilentlyContinue
#>

param([switch]$Force)   # -Force: ignore Tune.AutoStart (used by the desktop icon)

$ErrorActionPreference = 'SilentlyContinue'

# Honor Tune.cs "public const bool AutoStart = ..." unless -Force was passed.
if (-not $Force) {
    $tuneFile = Join-Path $PSScriptRoot "..\Tune.cs"
    $m = Select-String -Path $tuneFile -Pattern 'AutoStart\s*=\s*(true|false)'
    if ($m -and $m.Matches[0].Groups[1].Value -eq 'false') {
        # Auto-start is off: exit cleanly. No relaunch loop.
        return
    }
}
$RelaunchDelaySec = 5

while ($true) {
    $exe = Get-ChildItem "C:\KMotion*\KMotion\Release64\iMachKflop.exe" |
           Sort-Object LastWriteTime -Descending |
           Select-Object -First 1

    if ($exe) {
        Start-Process -FilePath $exe.FullName -WorkingDirectory $exe.DirectoryName -Wait
    }

    Start-Sleep -Seconds $RelaunchDelaySec
}
