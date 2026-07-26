# Vendor Manual

This project follows the **VistaCNC P4-S(E) LinuxCNC manual v1.1** (matching the
pendant's LinuxCNC firmware, FW v200). It is the reference for the USB report
layout, the button bitmaps, and the confirm grammar — every key behavior in this
bridge mirrors it.

The manual is a third-party (VistaCNC) copyrighted document and is **not**
redistributed here. Download it from the vendor:

- VistaCNC downloads: https://www.vistacnc.com  (look for the P4-S LinuxCNC
  package / manual)

> **Use the LinuxCNC edition only.** The Mach3 edition of the same manual documents
> the *opposite* EN/function-key order and will send you the wrong way. This project
> does not use Mach3.

A decoded, self-contained summary of everything this bridge depends on (endpoints,
both bitmaps, the hold-then-EN grammar, the LCD frame) is in `../KEYMAP.md`, so you
can understand and adapt the bridge without the PDF in hand.
