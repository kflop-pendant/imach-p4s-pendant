# Example KFLOP init programs (machine-specific reference)

**These are the reference machine's init programs, not a drop-in for yours.** They are
included as *worked examples*: they show the axis setup for a Bridgeport-style SuperMax
knee mill (switchable knee/quill on Z, dual spindle) and — most importantly — **how an
init tells the pendant bridge what it needs to know.**

Your machine's counts-per-inch, channels, limits, spindle, and trajectory-planner values
will differ. Copy the *pattern*, not the numbers.

## What the bridge requires from your init

The bridge reads two values from KFLOP UserData (double-indexed). Publish them near the
**end** of your init, after setup completes — see where these examples call them, just
after the trajectory-planner (`SetTPParameter`) block:

| UserData (double index) | Meaning |
|---|---|
| **`54` — config id** | Which physical axis is on Z. `1` = quill on ch2, `2` = knee on ch2. The bridge resolves the Z/C pair's counts-per-inch, speed cap, and accel from this. If your machine has a single fixed Z, just publish `1`. This value is also the bridge's **"an init has run"** signal — until it appears, the bridge waits. |
| **`58` — init identity** | A distinct number per init so the pendant can name the loaded config on its LCD banner: here `1` = Standard, `2` = Knee Z, `3` = PCB. Assign your own. It is a *separate* value from `54` because two configs can share a config id (Knee Z and PCB are both `54 = 2`) yet want different names. Optional — without it the pendant simply won't name the init. **Also write the negative (`-id`) as the very first thing in `main()`**: the pendant then shows "<name> / Loading" during the load (including a reload of the same init), and `InitPrompt.c` / `InitGate.c` use `58` to tell "no init since power-up" (`0`) from "loading" (`< 0`) and "loaded" (`> 0`). |

Minimal example:

```c
int main()
{
    SetUserDataDouble(58, -1.0);  // FIRST: identity negative = "Standard is loading"
    // ... your axis / spindle / TP setup ...
    SetUserDataDouble(54, 1.0);   // config id: quill on Z
    SetUserDataDouble(58, 1.0);   // init identity: "Standard" (loaded)
    // ... forever loop ...
}
```

> ⚠ **Do not reuse UserData 56/57 for anything else.** They are the init↔PendantService
> `TP_HOLD_REQ` / `TP_HOLD_ACK` handshake that serializes access to the KMotionCNC PC_COMM
> channel. Publishing identity on 56 (as an early draft did) collides with that handshake.
> See [`../pendant/README.md`](../pendant/README.md) for the full UserData map.

## The files

| File | Configuration |
|---|---|
| `JPB - Standard.c` | Quill on Z. Config id `1`, identity `1`. The everyday setup. |
| `JPB - Knee Z.c` | Knee on Z (so unedited G-code can use full knee travel). Config id `2`, identity `2`. |
| `JPB - PCB.c` | Knee on Z, high-speed spindle, tuned for PCB isolation milling. Config id `2`, identity `3`. |
| `DriveResetAtStartup.c` | Power-on program (thread 1) that holds the stepper drives disabled until an init loads. |
| `InitGate.c` | Optional confirmation gate for the screen's init buttons — see below. |

Each init `#include "../shared/KflopToKMotionCNCFunctions.c"`, so keep the `shared/`
folder one level up from here.

## Custom-screen extras in these inits (optional)

The reference machine switches inits from **buttons on its KMotionCNC custom screen**, with
a `DROLabel` readout (persist **Var 170**) beside them. The inits and helpers drive that
readout; all of it is optional and harmless without such a screen.

- **Loading feedback.** Each init writes "`<NAME> LOADING...`" to Var 170 as its first
  statement, then "`<NAME> init LOADED`" (or "`TP FAIL!!`") after its trajectory-planner
  block. Every Var 170 write uses gather offset **1100**, clear of `PendantService.c`'s
  Var 172 lock label (1000) and its MDI buffer (1024).
- **Fresh DROs on every load.** Inside the `TP_HOLD` block each init does KMotionCNC's native
  Set-to-0 (`PC_COMM_SET_X + axis`) for X/Y/Z/A/C, clearing any G92 / fixture offset left from
  a zero button — offsets persist in `emc.var`, so without this an axis zeroed in an earlier
  session comes up non-zero. (Loading an init therefore always clears the work zero.)
- **"CHOOSE init ->" blink.** `pendant/InitPrompt.c`, launched by the bridge before any init is
  loaded, blinks Var 170 until one starts. Make your screen's seed text for that readout
  "CHOOSE init ->" so it reads the same between blinks.

### `InitGate.c` — confirm before switching inits

Loading an init restarts the drives and (with the above) zeroes the DROs, so a stray click
or hotkey on a set-up machine loses the work zero. The gate asks first — **only when an init
is already loaded**:

| State (UserData 58) | Gate does |
|---|---|
| a G-code job is running (`JOB_ACTIVE`) | refuses, no prompt |
| `0` — no init since KFLOP power-up | loads immediately, no prompt |
| `> 0` — an init is loaded | Yes/No box (default **No**) naming the loaded init |
| `< 0` — an init is mid-load or was interrupted | sharper Yes/No (default No) |

KMotionCNC setup (**Tool Setup → M-codes**, with KMotionCNC's own config otherwise unchanged):

1. Move each init to an unused M-code — here **M110 / M111 / M112** → Exec Prog, **Thread 4**,
   the init file.
2. Point each init **button** at the gate — Exec Prog, **Thread 6**, **Var 160**, `InitGate.c`.
   KMotionCNC writes the button's action slot (11 / 12 / 13 for user buttons 0 / 1 / 2) into
   persist int 160, which is how the gate knows which init was requested.
3. If you use other M-code numbers, change `INIT_MCODE_BASE` in `InitGate.c`. Note that
   `PC_COMM_MCODE` takes the **M-code number** (110), not the action slot (31) — numbers
   24–99 map to KMotionCNC's *special* actions instead.

The gate's message box and its `PC_COMM_MCODE` call use the single PC_COMM mailbox, so they
run inside the same `TP_HOLD` handshake as the inits: `PendantService.c` pauses (pendant
frozen while the box is open; the hardware E-stop watchdog on thread 5 is unaffected).
Thread 6 must be run-once only — never a thread hosting a forever-loop program.
