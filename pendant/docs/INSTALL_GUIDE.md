# Installing the VistaCNC iMach III P4-S Pendant on a KFLOP / KMotionCNC Machine

> STATUS: Reference install procedure. Reflects the shipping bridge — portable
> paths (no hardcoded locations), external `pendant.conf` for tuning, the USB-drop
> jog watchdog, and live init tracking.

## 0. Governing principle (read first)
The VistaCNC iMach III P4-S manual (**LinuxCNC edition**) is the single source of
truth for how every key behaves. All pendant-operating code (the C# bridge and
PendantService.c) and every word of this guide must agree with it.

- Authority copy: ./manuals/vistacnc-imach-p4s-linuxcnc-v1.1.pdf (matches
  pendant firmware v200).
- What we mirror from it: the FUNCTION of each key and the confirm grammar.
- **Do NOT use the Mach3 edition of the manual as a reference** — it documents
  the OPPOSITE EN/function-key order and will send you the wrong way.
- Confirm grammar (LinuxCNC manual §4.1, §4.2, §4.3 and the F1/F2/F3 sections):
  HOLD the function/action key, THEN tap EN (Enable) to execute. Releasing
  without tapping EN does nothing. E-Stop is the exception — it acts
  immediately.

## 1. What this is
A direct pendant -> KFLOP/KMotionCNC integration. No Mach3, no Mach3 plugin.
The pendant is purely an I/O + display device; KFLOP runs motion; the KMotionCNC
process runs the G-code interpreter (work offsets, tool table, DROs).

## 2. Prerequisites
- VistaCNC iMach III P4-S pendant
- KFLOP (with Kanalog, if used) running under **KMotionCNC 5.4.4 or newer** (earlier
  versions lack the trajectory-planner SET/GET fix the init relies on)
- The Windows PC that runs your CNC software (the machine PC). The original
  conversion was flashed and driven from a Windows 11 machine PC.
- Downloads: VistaCNC "LinuxCNC P4-S Driver + Installation Package v1.20"
  (vc-p4s_linuxCNC.zip -- contains the LinuxCNC firmware .hex and the Pendant FW
  Loader); Zadig (WinUSB installer).

## 3. Step 1 -- Flash firmware: Mach3 -> LinuxCNC (P4S CNC FW v200)
1. Extract vc-p4s_linuxCNC.zip; locate the LinuxCNC firmware .hex and the
   Pendant FW Loader.
2. Enter the bootloader: HOLD the MPG wheel between two detents while plugging
   in the USB cable.
3. Run the Pendant FW Loader and flash the LinuxCNC .hex.

Result: USB PID stays 0x04D8/0xFCE8; the input report grows from 4 bytes to 8;
the LCD protocol changes to the LinuxCNC format.
Reversible: the Mach3 firmware is a separate VistaCNC download.

## 4. Step 2 -- Windows driver: HID -> WinUSB (Zadig)
Why: Windows' HID stack accepts the LCD output reports but never renders them --
the display won't update. The LCD requires raw libusb interrupt transfers, so
the pendant must be on the WinUSB driver.
1. Run Zadig. Options -> List All Devices; select the pendant
   (VID 0x04D8 / PID 0xFCE8, interface 0).
2. Choose WinUSB and install/replace the driver.

After this, both the bridge (LibUsbDotNet) and Python test tools (pyusb) can
read input AND drive the LCD.

## 5. Step 3 -- KFLOP-side service
`PendantService.c` is deployed automatically **next to the bridge exe** (into
`<KMotion>\KMotion\Release64`) by the build, and the bridge loads it from its own
directory at runtime -- you don't place it by hand. KFLOP compiles it when the
bridge connects; there is no separate build step for it.

> ⚠ **THREAD ASSIGNMENTS ARE LOAD-BEARING — do not reassign them.**
> Three threads are reserved for long-running programs, each on its own thread so
> they can all run at once:
> - **Thread 4 — the active init program** (launched from the KMotionCNC screen).
> - **Thread 5 — `EStopWatch.c`, the dedicated E-stop watchdog.** The bridge loads
>   it here and stops it with `KillProgramThreads(5)`. It does nothing but watch the
>   E-stop and stop motion, so it can never be starved.
> - **Thread 7 — `PendantService.c`.** The bridge loads it here and stops it with
>   `KillProgramThreads(7)`.
>
> **Never bind any M-code, S-word, or logging program to threads 4, 5, or 7.**
> KFLOP overwrites whatever already occupies a thread when a new program is loaded
> onto it. The trap: if a spindle M-code (or any C program a job triggers) is bound
> to the init's thread, the **first such M-code of a job evicts the init mid-run** —
> and any E-stop or safety loop living inside that init dies silently with it, so the
> E-stop stops working *only while a job is running*. That failure mode is exactly
> why the E-stop watchdog lives alone on its own thread (5). Keep all M-codes,
> S-words, and loggers on threads **1, 2, 3, or 6**.
>
> Separately: click-to-loading an init while its predecessor's `SetTPParameter`
> block is still running can kill a thread mid-write and leave the trajectory planner
> half-configured (Z/C counts-per-inch or Vel/Accel stuck at one axis's value). Wait
> for the "init LOADED" screen cue before loading the next init.

## 6. Step 4 -- The bridge (iMachKflop)
- .NET Framework 4.8, x64, LibUsbDotNet. Set `<KMotionRoot>` in
  `pendant\iMachKflop.csproj` to your KMotion install, then `dotnet build` -- it
  compiles to `<KMotion>\KMotion\Release64` and deploys `PendantService.c` and
  `pendant.conf` alongside the exe.
- Runs from `<KMotion>\KMotion\Release64`; it finds `PendantService.c` and
  `pendant.conf` in its own directory (no hardcoded paths).
- Auto-starts via a Scheduled Task ("PendantBridge") at logon (register it once
  with `autostart\install-pendant-task.ps1`), gated so it waits for KMotionCNC to
  be running before it connects.

### Download & run (no building)
Most users skip the build: download the latest zip from the repo's **Releases** page and extract its
contents into `<KMotion>\KMotion\Release64`. Two prompts to expect:
- **During extraction:** Windows asks to replace **`KflopToKMotionCNCFunctions.c`** — click
  **Replace**. It's Dynomotion's helper (ships with KMotion, and is in the zip too); the copies are
  identical, so replacing is correct.
- **On first launch — SmartScreen:** the exe is unsigned open-source, so Windows may show *"Windows
  protected your PC."* Click **More info** first — *then* the **Run anyway** button appears. (To skip
  this, right-click the downloaded zip → **Properties → Unblock** before extracting.)

## 6a. Tuning -- pendant.conf (no rebuild)
Speed/feel values (IPM caps, jog/step accels, step sizes, velocity/continuous
tuning, half-speed, GOTOZ feeds, spindle-override range, DRO decimals) live in
`pendant.conf` next to the exe. **Edit it and restart the bridge -- no rebuild:**
`autostart\bridge-control.ps1 -Action Restart` (or `-Action Console` to watch it
load; every value is echoed). It is validated at startup: a missing, malformed,
out-of-range, duplicate, or unknown key makes the bridge refuse to start
(`CONFIG/ERR` on the LCD, the offending key on the console) -- it never guesses a
speed. The build seeds `pendant.conf` only if it isn't already next to the exe, so
your edits survive rebuilds. **None of the shipped numbers will match your
machine** -- set counts-per-inch in `Tune.cs` and speeds/accels in `pendant.conf`.

## 7. Step 5 -- First run
The pendant comes alive only when **all three** of these are up (order doesn't matter):
1. **The bridge is running** — the "PendantBridge" task auto-starts it at logon, or launch it
   manually (`bridge-control.ps1 -Action Console`, or double-click `iMachKflop.exe`).
2. **KMotionCNC is running** (and the machine homed as normal).
3. **An init is loaded** — click your init button on the KMotionCNC screen; it publishes the
   `UserData 54` contract the bridge waits for.

Watch the pendant LCD move through these stages, so you always know where you are:
- **`LinuxCNC`** — the firmware's idle screen; the bridge isn't driving the pendant yet (it's not
  running, or the KFLOP isn't present).
- **waiting for KMotionCNC** — the bridge is up and waiting for the KMotionCNC process.
- **waiting for an init** — KMotionCNC is up; load an init.
- **live DRO** — connected and operational.

Then confirm MPG jog, axis/mode select, and the function buttons per the manual grammar (HOLD the
function key, tap EN).

## 7a. Updating KMotion later (don't skip this)
KMotion installs **each version to its own folder** (e.g. `C:\KMotion5.4.5\`). A fresh install has
**none of your setup** — no init buttons, no M-code/thread assignments, no custom screen — and its
`Release64` has no bridge. So after updating KMotion:
1. **Re-extract the bridge zip** into the new version's `<KMotion>\KMotion\Release64`.
2. **Copy your KMotionCNC config** from the *old* install's `Data` folder to the *new* one's — with
   KMotionCNC closed, and after backing up the new copies:
   `GCodeConfigCNC.txt` (your Tool Setup: M-codes, init buttons, threads, screen reference),
   `emc.var` (work offsets), `Default.tbl` (tool table), `persistCNC.ini`, and `GFilesCNC.txt`.

Your inits, M-codes, and screen are referenced by absolute path, so they carry over once the config
points to them. The same bridge build works across KMotion versions — no rebuild needed.

## 8. Operating reference
See ./KEYMAP.md and the vendor manual (linked in ./manuals/README.md). All key
behavior follows the manual; confirm grammar is hold-then-EN (hold the function
key, then tap EN). The manual's double-tap "button jog" on F1/F2/F3 is
intentionally not implemented -- see ../README.md.

Adapting to a different pendant: `iMachKflop.exe --btnmap` prints the raw report
and decoded bits so you can remap the buttons; `--ledmap` sweeps the LCD indicator
byte. Both open only the pendant -- no KFLOP or machine power needed.

## Still to polish (docs only)
- [ ] Exact Zadig screenshots and a Scheduled-Task walkthrough.
