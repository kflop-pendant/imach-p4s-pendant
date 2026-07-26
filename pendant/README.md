# VistaCNC iMach III P4-S Pendant → KFLOP / KMotionCNC

A working bridge that drives a **VistaCNC iMach III P4-S USB pendant** against a
**Dynomotion KFLOP** running under **KMotionCNC** — replacing the Mach3 plugin the
pendant ships with.

Machine it runs on: a Bridgeport-style SuperMax knee mill, converted from Mach3.
Six channels (X, Y, knee, quill, rotary A, spare B), closed-loop steppers except the
open-loop rotary.

---

## ⚠ Safety

The pendant E-stop button behaves very differently depending on your pendant model
and how it's wired. **Read this before assuming it's your primary safety device.**

**On this reference machine (P4-SE + 2-wire loop landed in a hardware chain).** The
P4-SE's E-stop button is 2-pole — one pole reports over USB, the other is a
dry-contact loop out through the EE box (LinuxCNC-edition manual v1.1 §5.2). That
loop is landed in the machine's 24V E-stop chain, which passes through an SSR and
into both VFDs' 24V E-stop inputs. Pressing E-stop opens the chain, and **the
spindle stops in hardware**, independent of KFLOP, KMotionCNC, and the pendant
bridge. The same chain feeds KFLOP's opto bit 143; the init C program watches that
bit in its forever loop and calls `DisableAxis(0..5)`. **Axis stop is software** —
if the init is not running (KMotionCNC not open, init not loaded, or the init
killed by a thread collision), the axes will NOT stop. Recovery is deliberate:
axes stay disabled after release, re-enable via the pendant Control Lock (F3) or
by reloading the init.

**On a plain P4-S (no EE box).** There is no 2-wire loop. **The pendant E-stop is
software-only** — it asserts a bit in the USB report, and nothing happens unless
the bridge sees it and acts on it. Do not treat it as a primary safety device.

**On a P4-SE without the 2-wire loop wired in.** Same as above — software-only. The
pendant model doesn't help unless you've completed the wiring into a hardware chain.

**General principle.** The primary safety device on any machine is a hardwired
physical E-stop button wired directly into the drive/VFD enable chain, independent
of any computer, USB, or software program. The pendant is a convenience; the
hardwired button is the guarantee.

---

## How it fits together

```
  Pendant (USB HID)
        |  8-byte input report / 19-byte LCD frame
        v
  iMachKflop.exe  ...... C# bridge, runs on the PC
        |  KMotion .NET API
        v
  KFLOP  ............... PendantService.c on thread 7
        |  UserData cells (shared memory)
        v
  KMotionCNC  ......... G-code interpreter, DROs, custom screen
```

The pendant talks **directly to the KFLOP**, not through KMotionCNC. The bridge is a
relay: it decodes the pendant, does the jog math, and posts commands into KFLOP
UserData. `PendantService.c` is the single owner of the command cell on the KFLOP side
and executes what it finds there. KMotionCNC is used for what it's good at — the
interpreter, work offsets, the tool table — and for a small custom-screen readout.

**Prerequisite:** the pendant must be on the **WinUSB** driver (set once with Zadig).
It is *not* usable as a plain HID device by this bridge.

---

## The C# bridge — `pendant/`

| File | What it does |
|---|---|
| `Program.cs` | Entry point. Waits for KMotionCNC to be running, then for an init program to have loaded, before connecting — so it sits quietly instead of flapping if you're just using the PC. Opens the pendant, builds the link, runs the bridge, exits cleanly on a lost KFLOP link **or a lost pendant** (the input watchdog). Loads `PendantService.c` and `pendant.conf` **from its own directory** (the build deploys both there), so no machine-specific paths are compiled in. |
| `Pendant.cs` | USB transport and protocol. The 8-byte input report (buttons, mode bits, MPG wheel) and the 19-byte LCD output frame, including the activity counter the firmware needs to treat each frame as new. The byte map was reverse-engineered and hardware-validated. |
| `Bridge.cs` | The main loop and all the behaviour. Axis selection (the three buttons each toggle a *pair*), mode selection, jogging (step / velocity / continuous), the hold-then-EN command grammar, LCD line composition, the machine-idle gate, and servicing of the custom-screen toggle. |
| `KflopLink.cs` | Everything that touches the KFLOP: the UserData memory map, command posting, DRO reads, jog and step math per axis, the `MachineIdle` detector, and loading `PendantService.c` onto thread 7. |
| `pendant.conf` | **The file you tune.** A plain `key = value` text file of the speed/feel values — IPM caps, jog/step accels, step sizes, velocity/continuous tuning, half-speed, GOTOZ feeds, spindle-override range, DRO decimals. **Edit it and restart the bridge — no rebuild.** It is loaded from the exe's directory and validated at startup; a missing/malformed/out-of-range/unknown key makes the bridge refuse to start (`CONFIG/ERR` on the LCD, the offending key named on the console) rather than guess a speed. |
| `Tune.cs` | The compiled config: the *structural* and *measured* settings that don't change at runtime — per-axis table (counts/unit, channel, enable flag, rotary flag), the counts-per-inch machine facts, per-mode and per-button enable tables, gate/watchdog timings, every LCD string, and the auto-start flag. Speed/feel numbers moved out to `pendant.conf`; Tune holds the shipped defaults and everything not meant to be hand-tuned. |
| `TuneConfig.cs` | Loads and strictly validates `pendant.conf` into `Tune` at startup — fail-loud, no silent fallback to defaults, every applied value echoed to the console. |
| `PendantService.c` | The KFLOP-side half. Runs as a C program on thread 7: publishes the DROs and the axis-enable state into UserData every loop, executes commands the bridge posts (zero, go-to-zero, spindle, overrides, control lock), keeps a heartbeat so the link watchdog doesn't false-trip, and writes the custom-screen status labels. Deployed next to the exe by the build. |
| `iMachKflop.csproj` | .NET Framework 4.8, x64. Builds to the KMotion `Release64` folder and deploys `PendantService.c` + `pendant.conf` alongside the exe. |

### Auto-start — `pendant/autostart/`

| File | What it does |
|---|---|
| `install-pendant-task.ps1` | Run once. Registers the `PendantBridge` scheduled task: at logon, run the wrapper in a hidden window, no execution time limit, no duplicate instances. The task runs **non-elevated, as you** — only the registration may need an admin shell. It also exports the task definition to `PendantBridge.task.xml`, so the setup is version-controlled rather than living only in Task Scheduler. |
| `PendantBridge.task.xml` | The exported task definition, written by the install script. Keeps the scheduled-task configuration in the repo. |
| `run-pendant.ps1` | Supervisor loop. Finds the newest `iMachKflop.exe` under any `C:\KMotion*\...\Release64` (so a KMotion upgrade needs no edit), launches it, and relaunches a few seconds after any exit — e.g. after a KFLOP power-cycle, which the bridge deliberately treats as fail-safe-and-exit. Honours the `AutoStart` flag in `Tune.cs`. |
| `bridge-control.ps1` | Start / stop / restart from a desktop icon, for when auto-start isn't wanted or didn't take. `-Action Console` runs the bridge in a **visible window**, which is the way to see the compile line and diagnostics — the supervised launch is hidden. |

### Diagnostic / bring-up modes

Run the exe directly with one of these flags — no KFLOP or machine power needed. They exist
so you can adapt the bridge to a *different* pendant, and to see the raw wire format.

| Flag | What it does |
|---|---|
| `iMachKflop.exe --btnmap` | Opens only the pendant and prints the raw 8-byte input report plus decoded button/mode bits, so you can map which bit each button (and each half of the split buttons) sends. Essential if you're porting to another VistaCNC model. |
| `iMachKflop.exe --ledmap` | Opens only the pendant and sweeps the byte-17 LCD indicator so you can find the on-screen indicator values by hand. |

---

## Optional: KMotionCNC screen integration

The reference machine also mirrors the pendant's **Control Lock** on its KMotionCNC custom
screen. A screen button runs a tiny KFLOP program that only raises a request flag at
`UserData 66` — it never writes the command cell, so it can't race `PendantService.c`. The
bridge polls that flag, applies the *same* machine-idle gate the pendant's F3 uses, and
toggles. Clicking the screen and pressing F3 therefore drive one identical, idle-protected
path.

The screen definition itself is machine-specific and isn't shipped here; the only
integration point you need is **`UserData 66`** (screen → bridge, `1` = toggle requested,
bridge clears it).

---

## Machine init programs — `../example-inits/`

Reference examples live in [`../example-inits/`](../example-inits/) (with a walkthrough of
the init contract in its README). These are the usual KFLOP init programs (axis parameters,
coordinate system, limit watching). They are **not** part of the pendant work, but they
carry two small additions the pendant relies on:

- a **config id** (UserData 54: 1 = quill on Z, 2 = knee on Z) telling the bridge which
  physical axis is on which channel (this mill can put either the knee or the quill on Z);
- an **init identity** (UserData 58: 1 = Standard, 2 = Knee Z, 3 = PCB) so the pendant can
  name the loaded init on its LCD banner — a separate value from the config id because
  Knee Z and PCB share config id 2 and would otherwise be indistinguishable;
- a **`DROLabel` call** that names the loaded init on the custom screen.

| File | What it does |
|---|---|
| `JPB - Standard.c` | Quill on Z. The everyday configuration. |
| `JPB - Knee Z.c` | Knee on Z, so unedited G-code can use the full knee travel. |
| `JPB - PCB.c` | Knee on Z, tuned for PCB isolation milling. |

---

## Shared — `shared/`

| File | What it does |
|---|---|
| `KflopToKMotionCNCFunctions.c` | **Dynomotion's own** helper (not ours). Provides `DoPC`, `DROLabel`, and friends. Any file that includes it must `#define TMP <n>` first, giving it a scratch UserData cell. |

---

## What you get on the pendant

- **Jogging** — step, velocity, and continuous modes, with the MPG wheel and the
  selectable step sizes.
- **Axis select** — the three buttons toggle pairs (X/A, Y/B, C/Z), matching the
  silkscreen.
- **Feed and spindle overrides**, spindle on/off.
- **Cycle start / feed hold / halt / E-stop.**
- **F1 = zero the work offset** on the selected axis.
- **F2 = go to zero** — retracts Z clear, then moves X and Y home.
- **F3 = control lock** — enables/disables all axes, mirrored on the screen.
- Function buttons use a **hold-then-tap-EN** grammar: hold the button, the LCD shows
  what it will do, tap EN to commit. Nothing fires on a single press.
- **Live init tracking** — load a different machine init (Standard / Knee Z / PCB) and the
  bridge re-resolves knee-vs-quill on the fly and flashes the init name on the LCD for
  ~2 s. No bridge restart, and the swap is deferred until the machine is idle so it never
  changes a jogging axis's parameters underneath it.
- **USB-drop jog watchdog** — if the pendant's USB input stalls mid-jog, the bridge stops
  the axis (the KFLOP is a separate device, so it can still command the stop) and, on a
  sustained loss, exits cleanly so the supervisor relaunches and re-opens the pendant.
- Every mode and button can be **individually disabled** in `Tune.cs`. A disabled button
  is fully inert. E-Stop and EN are never disableable, and the bridge refuses to start
  if you've turned off every jog mode.

**Deliberately *not* implemented — button jogging.** The VistaCNC manual (§3.1) gives F1/F2/F3
a second function: double-tap-and-hold to jog an axis at the continuous rate (F1 = Z+,
F2/F3 = the selected X or Y ±). It's intentionally left out — it duplicates the MPG wheel,
covers the axes asymmetrically (Z+ only, no Z−), and layering an easy-to-mistrigger
continuous-jog gesture onto the ZERO and GOTOZ buttons runs against the safety grain. The
wheel already jogs every axis in both directions.

---

## Things worth knowing before you take this on

- **The pendant needs WinUSB (Zadig).** Non-negotiable, and it means the pendant no
  longer works with its Mach3 plugin unless you switch the driver back.
- **The protocol follows VistaCNC's own LinuxCNC-edition manual** (v1.1, matching the
  pendant's FW v200), validated against the hardware. That manual is the reference for
  the report layout, the button bitmaps, and the confirm grammar. Note the **Mach3**
  edition of the same manual uses the *opposite* EN/function-key order — don't use it as
  a reference for this project. A different pendant model will differ again.
- **`Tune.cs` is where all the machine-specific numbers live.** Counts per unit, max
  rates, accelerations, which axes exist. None of it will match your machine as shipped.
- **One command cell, one owner.** `PendantService.c` is the only thing that writes the
  command request cell. Anything else that wants to command the machine — like the screen
  button — raises a flag and lets the bridge relay it. Adding a second writer will bite
  you.
- **The inits touch the same channel too — `SetTPParameter`.** The KMotionCNC PC_COMM
  channel (cells 100–107) has a single mailbox and no arbitration between KFLOP threads.
  When an init (thread 4) calls `SetTPParameter` while `PendantService.c` (thread 7) is
  polling DROs, the two commands race that one mailbox and the loser silently reads the
  winner's ack as its own success — no error surfaces, because the forgery happens in
  KFLOP memory (Tom Kerekes' 2026-07-24 diagnosis). So the "single owner" rule extends
  past the pendant command cell to **every** PC_COMM user.
- **TP_HOLD handshake serializes them.** Before its `SetTPParameter` block, an init sets
  `TP_HOLD_REQ` (UserData 56) and waits for `TP_HOLD_ACK` (57). `PendantService.c` checks
  `TP_HOLD_REQ` at the top of every pass; when set it acks and pauses its own PC_COMM
  polling until the init clears the request (with a 2 s fail-safe timeout on the init side
  so it proceeds harmlessly if the service isn't running). DRO updates pause for the ~0.6 s
  the TP block takes, then resume. If you add another PC_COMM writer, it must join this
  handshake.
- **`DROLabel` passes its string through the gather buffer.** If you arm a gather-based
  diagnostic that overlaps the offsets in use, your screen labels will silently stop
  updating. Learned the hard way.
