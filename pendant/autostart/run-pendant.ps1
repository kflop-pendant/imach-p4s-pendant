<#
  run-pendant.ps1
  Keeps the iMach P4-S -> KFLOP pendant bridge running.

  Launches iMachKflop.exe and waits. When it exits (e.g. after a KFLOP
  power-cycle -- which the bridge handles by failing safe and exiting), waits a
  few seconds and relaunches. Installed as the "PendantBridge" Task Scheduler
  task that runs at logon (see install-pendant-task.ps1).

  The exe path is auto-detected as the newest-built iMachKflop.exe under any
  C:\KMotion*\KMotion\Release64, so a KMotion upgrade needs no change here.
  NOTE: "newest" means newest FILE DATE. If you keep more than one KMotion
  install, a stale build in the OLD tree can win and will fail to load its
  KFLOP programs against the running server -- keep only the live install, or
  make sure the live one holds the newest exe.

  WHY -PassThru + WaitForExit() INSTEAD OF -Wait (2026-09-22): "Start-Process
  -Wait" waits on the child AND on descendants that inherit its handles, and
  was observed still waiting after the bridge was gone -- leaving the
  supervisor alive but never relaunching (looks healthy in Task Manager, no
  pendant). Waiting on the returned process object instead returns exactly when
  that process exits. The loop also now logs, so a future stall is diagnosable
  rather than silent, and never dies on an unexpected error.

  Log: %LOCALAPPDATA%\PendantBridge\run-pendant.log (rotated at ~1 MB).

  To stop it (e.g. during development):
     Stop-ScheduledTask -TaskName PendantBridge
     Stop-Process -Name iMachKflop -Force -ErrorAction SilentlyContinue
  or:  bridge-control.ps1 -Action Stop
#>

param([switch]$Force)   # -Force: ignore Tune.AutoStart (used by the desktop icon)

Set-StrictMode -Off
$ErrorActionPreference = 'Stop'   # surfaced via try/catch below; the loop never exits

$LogDir  = Join-Path $env:LOCALAPPDATA 'PendantBridge'
$LogFile = Join-Path $LogDir 'run-pendant.log'
$LogMaxBytes = 1MB

function Write-Log([string]$Message) {
    # Logging must never take the supervisor down.
    try {
        if (-not (Test-Path $LogDir)) { New-Item -ItemType Directory -Force -Path $LogDir | Out-Null }
        if ((Test-Path $LogFile) -and ((Get-Item $LogFile).Length -gt $LogMaxBytes)) {
            Move-Item $LogFile "$LogFile.old" -Force
        }
        "{0:yyyy-MM-dd HH:mm:ss}  {1}" -f (Get-Date), $Message | Add-Content -Path $LogFile -Encoding utf8
    } catch { }
}

# Honor Tune.cs "public const bool AutoStart = ..." unless -Force was passed.
# If Tune.cs can't be read we RUN (fail-open), matching the previous behavior.
if (-not $Force) {
    try {
        $tuneFile = Join-Path $PSScriptRoot '..\Tune.cs'
        $m = Select-String -Path $tuneFile -Pattern 'AutoStart\s*=\s*(true|false)' -ErrorAction Stop
        if ($m -and $m.Matches[0].Groups[1].Value -eq 'false') {
            Write-Log 'AutoStart=false in Tune.cs and -Force not passed; exiting without starting the bridge.'
            return
        }
    } catch {
        Write-Log "Could not read Tune.cs AutoStart ($($_.Exception.Message)); starting anyway."
    }
}

$RelaunchDelaySec = 5      # normal wait between runs
$FastExitSec      = 3      # a run shorter than this counts as a failed start
$MaxDelaySec      = 60     # cap on the backoff when it keeps failing

$fastExits = 0
Write-Log "Supervisor started (pid $PID)."

while ($true) {
    $delay = $RelaunchDelaySec
    try {
        $exe = Get-ChildItem 'C:\KMotion*\KMotion\Release64\iMachKflop.exe' -ErrorAction SilentlyContinue |
               Sort-Object LastWriteTime -Descending |
               Select-Object -First 1

        if (-not $exe) {
            $fastExits++
            Write-Log 'No iMachKflop.exe found under C:\KMotion*\KMotion\Release64 -- is KMotion installed / the bridge deployed?'
        }
        else {
            $proc = Start-Process -FilePath $exe.FullName -WorkingDirectory $exe.DirectoryName -PassThru
            if (-not $proc) { throw 'Start-Process returned no process object' }

            Write-Log "Started $($exe.FullName) (pid $($proc.Id))."
            $t0 = Get-Date
            $proc.WaitForExit()          # returns exactly when THIS process exits
            $ran = ((Get-Date) - $t0).TotalSeconds

            $code = 'unknown'
            try { $code = $proc.ExitCode } catch { }
            Write-Log ("Bridge pid {0} exited after {1:N1}s (exit code {2})." -f $proc.Id, $ran, $code)

            if ($ran -lt $FastExitSec) { $fastExits++ } else { $fastExits = 0 }
        }
    }
    catch {
        $fastExits++
        Write-Log "ERROR in supervisor loop: $($_.Exception.Message)"
    }

    if ($fastExits -ge 3) {
        # Something is wrong (bad/mismatched build, missing KFLOP programs, no
        # exe). Back off so we don't spin, but KEEP TRYING so it self-heals.
        $delay = [Math]::Min($RelaunchDelaySec * $fastExits, $MaxDelaySec)
        Write-Log "$fastExits failed starts in a row; retrying in ${delay}s."
    }

    Start-Sleep -Seconds $delay
}
