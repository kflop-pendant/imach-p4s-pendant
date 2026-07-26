# iMach P4-S → KFLOP / KMotionCNC Pendant Bridge

A working bridge that drives a **VistaCNC iMach III P4-S USB pendant** against a
**Dynomotion KFLOP** running under **KMotionCNC** — a drop-in replacement for the
Mach3 plugin the pendant ships with.

The pendant becomes a first-class control device on a KFLOP machine: step / velocity /
continuous jogging on the MPG wheel, feed & spindle overrides, work-offset zero,
go-to-zero, control lock, and cycle start / hold / stop / E-stop — all following
VistaCNC's LinuxCNC-edition manual. Speed and feel are tuned in a plain-text
`pendant.conf` (**edit + restart, no rebuild**), and a **USB-drop watchdog** stops a
jog if the pendant's input stalls mid-motion.

Built and validated on a Bridgeport-style SuperMax knee-mill conversion (closed-loop
steppers, switchable knee/quill on Z, open-loop rotary A).

---

## ⚠ Safety — read before you rely on this

**The pendant E-stop is not automatically a hardware E-stop.** On a plain P4-S it is
*software only* — it asserts a bit over USB, and nothing stops unless the bridge sees it
and acts. Even on a P4-SE, the hardware stop only exists if you've wired its 2-wire loop
into your machine's E-stop chain. **Axis stop through this bridge is software** and
depends on KMotionCNC, the KFLOP init, and the bridge all running.

**The primary safety device on any machine is a hardwired physical E-stop wired directly
into the drive/VFD enable chain, independent of any computer, USB, or program.** This
pendant is a convenience; the hardwired button is the guarantee. The full safety
discussion — including how the reference machine wires its E-stop chain — is in
[pendant/README.md](pendant/README.md#-safety). Read it.

---

## Repository layout

| Path | What |
|---|---|
| [`pendant/`](pendant/) | The C# bridge, `PendantService.c` (the KFLOP-side program), `pendant.conf`, autostart scripts, and the docs. **Start here.** |
| [`shared/`](shared/) | Dynomotion's `KflopToKMotionCNCFunctions.c` helper. Ships with every KMotion install; included here with Dynomotion's permission so the repo builds standalone. |
| [`example-inits/`](example-inits/) | The reference machine's KFLOP init programs — **worked examples** that show how an init publishes the two UserData values the bridge needs. Machine-specific; not a drop-in. See [`example-inits/README.md`](example-inits/README.md). |

Both `pendant/PendantService.c` and the inits `#include "../shared/..."`, so keep
`pendant/`, `shared/`, and `example-inits/` at this top level.

---

## Quick start

1. **Pendant firmware + driver.** Flash the pendant to the LinuxCNC firmware (FW v200) and
   put it on the **WinUSB** driver with Zadig. (It will no longer work with the Mach3
   plugin until you switch back.)
2. **Build.** Point `<KMotionRoot>` in `pendant/iMachKflop.csproj` at your KMotion install,
   then `dotnet build`. The build deploys the exe + `PendantService.c` + `pendant.conf`
   into `<KMotion>\KMotion\Release64`; the bridge loads the last two from its own folder.
3. **Init contract.** Have your KFLOP init publish `UserData 54` (config id) and, optionally,
   `UserData 58` (init identity) — see [`example-inits/README.md`](example-inits/README.md).
4. **Run.** Register the logon task with `pendant/autostart/install-pendant-task.ps1`, or run
   `pendant/autostart/bridge-control.ps1 -Action Console` to watch it start.
5. **Tune.** Edit `pendant.conf` (next to the exe) and restart — no rebuild.

Full step-by-step: [pendant/docs/INSTALL_GUIDE.md](pendant/docs/INSTALL_GUIDE.md).

---

## Documentation

- [pendant/README.md](pendant/README.md) — how it fits together, the bridge internals, the full feature list, and safety.
- [pendant/docs/KEYMAP.md](pendant/docs/KEYMAP.md) — the authoritative USB report / button-bitmap decode (adapt this for a different pendant).
- [pendant/docs/INSTALL_GUIDE.md](pendant/docs/INSTALL_GUIDE.md) — setup from firmware flash to first run.

Adapting to a different pendant: `iMachKflop.exe --btnmap` and `--ledmap` map the raw
report and the LCD indicator with no KFLOP/machine power needed.

---

## Credits & license

- Bridge, `PendantService.c`, and the integration: **© 2026 Jim Barad**, MIT license (see [`LICENSE`](LICENSE)).
- `shared/KflopToKMotionCNCFunctions.c` is **Dynomotion's**, redistributed with permission; copyright remains Dynomotion's.
- Behavior follows **VistaCNC's P4-S LinuxCNC manual** (download link in [pendant/docs/manuals/README.md](pendant/docs/manuals/README.md)); the manual itself is not redistributed here.
