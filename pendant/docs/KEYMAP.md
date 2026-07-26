# iMach III P4-S — USB Report / Key Bitmap

The authoritative decode used by `Pendant.cs`.

Source: VistaCNC's **LinuxCNC-edition** manual (v1.1, matching pendant firmware
v200), validated against the hardware. Every bit below has been confirmed on a
real P4-S.

> **Do not use the Mach3 edition of the manual as a reference.** It documents the
> *opposite* EN/function-key order. This project follows the LinuxCNC edition
> throughout.

---

## USB identity and endpoints

| | |
|---|---|
| Vendor ID | `0x04D8` |
| Product ID | `0xFCE8` |
| Driver | **WinUSB** — set once with Zadig. The pendant will not work with this bridge on its stock HID/Mach3 driver. |
| Input | Interrupt endpoint `0x81` (IN), **8 bytes** |
| Output (LCD) | Interrupt endpoint `0x01` (OUT), **19 bytes** |

Read timeout is short (a few ms) and a short read is treated as "no new report" —
the pendant is polled, not awaited.

---

## IN report — 8 bytes

| Byte | Contents |
|---|---|
| 0 | MPG wheel counter. Free-running 8-bit; use the **signed delta** between reads and let it wrap (`(sbyte)(now - last)`). |
| 1 | unused |
| **2** | **Button bitmap** (see below) |
| **3** | **Mode / jog bitmap** (see below) |
| 4–7 | unused |

Buttons report as **level, not edge** — a bit stays set for as long as the button
is physically held. All edge detection is the host's job.

### Byte 2 — buttons

| Bit | Mask | Button |
|---|---|---|
| 0 | `0x01` | E-Stop |
| 1 | `0x02` | Spindle |
| 2 | `0x04` | Start (cycle start) |
| 3 | `0x08` | Stop |
| 4 | `0x10` | **EN** — the side modifier button |
| 5 | `0x20` | Axis select **X / A** |
| 6 | `0x40` | Axis select **Y / B** |
| 7 | `0x80` | Axis select **C / Z** |

### Byte 3 — modes and function keys

| Bit | Mask | Meaning |
|---|---|---|
| 0 | `0x01` | Mode button **S / Vv** (step jog / velocity jog) |
| 1 | `0x02` | Mode button **Cs / C%** (continuous jog / continuous-rate) |
| 2 | `0x04` | Mode button **F% / S%** (feed override / spindle override) |
| 3 | `0x08` | ⚠ **NOT A BUTTON** — a ~half-second blink/heartbeat bit. Ignore it. |
| 4 | `0x10` | **F1** (marked Z+) |
| 5 | `0x20` | **F2** (marked +X,Y) |
| 6 | `0x40` | **F3** (marked −X,Y) |
| 7 | `0x80` | unused |

**The `0x08` blink bit is the single most misleading thing in this report.** It
toggles on its own roughly twice a second. Treat byte 3 as a button field without
masking it out and you will see phantom presses.

---

## Button pairing

The pendant has fewer buttons than functions, so several buttons **toggle within a
pair** on each press. The pairings come from the silkscreen.

**Axis select — three buttons, six axes:**

| Button | Toggles between |
|---|---|
| `0x20` | X ↔ A |
| `0x40` | Y ↔ B |
| `0x80` | C ↔ Z — **selects Z first** (Z is the primary of this pair) |

**Mode select — three buttons, six modes:**

| Button | Toggles between |
|---|---|
| `0x01` | S (step jog) ↔ Vv (velocity jog) |
| `0x02` | Cs (continuous jog) ↔ C% (continuous rate) |
| `0x04` | F% (feed override) ↔ S% (spindle override) |

---

## The command grammar — hold, then tap EN

Function buttons do **not** fire on press. The sequence is:

1. **Hold** the function button (F1/F2/F3/Spindle/Start/Stop).
2. The LCD shows what the button *will* do — a preview, not an action.
3. **Tap EN** (rising edge) while still holding.
4. The command executes.

Releasing without tapping EN does nothing. This is deliberate: on a machine tool,
a single fat-fingered press should never start motion. Nothing in this bridge
commits on a lone button press.

E-Stop is the exception — it acts immediately, as it must.

### Not implemented: the double-tap button-jog

The silkscreen marks on F1/F2/F3 (`Z+`, `+X,Y`, `−X,Y`) are the manual's *second*
function for those keys — double-tap-and-hold to jog that axis at the continuous
rate (manual §3.1). This bridge **does not** implement it (see the "Deliberately not
implemented" note in `../README.md`). The keys are used only for their hold-then-EN
functions: **F1 = ZERO, F2 = GOTOZ, F3 = Control Lock**.

---

## LCD OUT report — 19 bytes

| Byte | Contents |
|---|---|
| 0–7 | Line 1 — exactly **8 characters**, space-padded, truncated if longer |
| 8–15 | Line 2 — exactly 8 characters, same rules |
| 16 | 0 |
| 17 | Axis/mode indicator byte (0 is safe) |
| 18 | **Activity counter** — increment on every frame |

**Byte 18 matters.** The firmware compares it against the previous frame and
ignores the write if it hasn't changed. Send a static value and the LCD appears
dead. Increment it (and let it wrap) on every frame and the display stays live.

---

## Adapting this to another pendant

The USB identity, the endpoints, and both bitmaps are specific to the P4-S.
Everything above lives in `Pendant.cs`, which is deliberately the only file that
knows the wire format — `Bridge.cs` sees decoded booleans, not bytes.

A different VistaCNC model will use the same *shape* (an 8-byte report, an LCD
frame with an activity counter) with different bit assignments. A different
manufacturer's pendant will not. In either case, `Pendant.cs` is the file to
rewrite, and nothing else should need to change.
