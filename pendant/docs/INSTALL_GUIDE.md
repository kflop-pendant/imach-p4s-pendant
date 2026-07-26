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
- KFLOP (with Kanalog, if used) running under KMotionCNC
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
> `PendantService.c` runs on **thread 7**; the bridge loads it there and stops it
> with `KillProgramThreads(7)`. The **init programs run on thread 4** when launched
> from the KMotionCNC screen. These are deliberately different threads so the
> service and an init can run at once. If you load another C program onto **thread 4
> or thread 7**, or click-to-load an init while its predecessor's `SetTPParameter`
> block is still running, you can kill a thread mid-write and leave the trajectory
> planner half-configured (Z/C counts-per-inch or Vel/Accel stuck at one axis's
> value). Both symptoms trace back to a thread collision. Keep thread 4 = inits,
> thread 7 = PendantService, and wait for the "init LOADED" screen cue before
> loading the next init.

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

## 7. Step 5 -- First-run verification
1. Start KMotionCNC and home the machine as normal.
2. The pendant LCD should come alive (leaving the "LinuxCNC----" idle screen).
3. Confirm MPG jog, axis select, and mode select.
4. Confirm the function buttons per the manual grammar (HOLD the function key, tap EN).

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
