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
| **`58` — init identity** | A distinct number per init so the pendant can name the loaded config on its LCD banner: here `1` = Standard, `2` = Knee Z, `3` = PCB. Assign your own. It is a *separate* value from `54` because two configs can share a config id (Knee Z and PCB are both `54 = 2`) yet want different names. Optional — without it the pendant simply won't name the init. |

Minimal example:

```c
// ... your axis / spindle / TP setup ...
SetUserDataDouble(54, 1.0);   // config id: quill on Z
SetUserDataDouble(58, 1.0);   // init identity: "Standard"
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

Each init `#include "../shared/KflopToKMotionCNCFunctions.c"`, so keep the `shared/`
folder one level up from here.
