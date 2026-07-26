using System;
using LibUsbDotNet;
using LibUsbDotNet.Main;

namespace iMachKflop
{
    /// <summary>
    /// Decoded snapshot of one 8-byte iMach III P4-S input report.
    /// Byte map per VistaCNC's LinuxCNC-edition manual (v1.1, pendant FW v200), hardware-validated on a P4-S.
    /// </summary>
    public sealed class PendantInput
    {
        public byte Mpg;     // byte 0: MPG wheel counter (use signed delta)
        public byte Btn;     // byte 2: button bitmap
        public byte Mode;    // byte 3: mode/jog bitmap

        // ---- byte 2 buttons ----
        public bool EStop   => (Btn & 0x01) != 0;
        public bool Spindle => (Btn & 0x02) != 0;
        public bool Start   => (Btn & 0x04) != 0;
        public bool Stop    => (Btn & 0x08) != 0;
        public bool En      => (Btn & 0x10) != 0;   // side button / modifier
        public bool AxisXA  => (Btn & 0x20) != 0;
        public bool AxisYB  => (Btn & 0x40) != 0;
        public bool AxisZC  => (Btn & 0x80) != 0;

        // ---- byte 3 modes / jog buttons (0x08 is the half-second blink, not a button) ----
        public bool ModeStep => (Mode & 0x01) != 0; // S / V
        public bool ModeCont => (Mode & 0x02) != 0; // C / F%
        public bool ModeF    => (Mode & 0x04) != 0; // F%
        public bool Zplus    => (Mode & 0x10) != 0; // Z+  (ZERO/F1 with En)
        public bool XYplus   => (Mode & 0x20) != 0; // +X,Y (GOTOZ/F2 with En)
        public bool XYminus  => (Mode & 0x40) != 0; // F3 / Machine ON-OFF (with En) -- hardware-confirmed bit
    }

    /// <summary>
    /// USB transport + protocol for the VistaCNC iMach III P4-S pendant.
    /// Input: interrupt EP 0x81, 8 bytes. Output (LCD): interrupt EP 0x01, 19 bytes.
    /// Requires the pendant on the WinUSB driver (set once with Zadig).
    /// </summary>
    public sealed class Pendant : IDisposable
    {
        private const int VID = 0x04D8;
        private const int PID = 0xFCE8;

        private UsbDevice _dev;
        private UsbEndpointReader _reader;
        private UsbEndpointWriter _writer;

        private byte _activity;       // byte 18, increments every LCD frame
        private byte _lastMpg;
        private bool _haveMpg;

        public bool Open()
        {
            _dev = UsbDevice.OpenUsbDevice(new UsbDeviceFinder(VID, PID));
            if (_dev == null) return false;

            // Whole-device setup is only needed for libusb-win32-style devices;
            // for WinUSB it's a no-op cast that returns null. Both are fine.
            IUsbDevice whole = _dev as IUsbDevice;
            if (whole != null)
            {
                whole.SetConfiguration(1);
                whole.ClaimInterface(0);
            }

            _reader = _dev.OpenEndpointReader(ReadEndpointID.Ep01);   // -> 0x81 IN
            _writer = _dev.OpenEndpointWriter(WriteEndpointID.Ep01);  // -> 0x01 OUT
            return _reader != null && _writer != null;
        }

        /// <summary>Read one input report. Returns null on timeout/short read.</summary>
        public PendantInput Read(int timeoutMs = 5)
        {
            byte[] buf = new byte[8];
            int xfer;
            ErrorCode ec = _reader.Read(buf, timeoutMs, out xfer);
            if (ec != ErrorCode.None || xfer < 8) return null;
            return new PendantInput { Mpg = buf[0], Btn = buf[2], Mode = buf[3] };
        }

        /// <summary>
        /// Diagnostic raw read: the full, unparsed 8-byte input report (null on
        /// timeout/short read). Used only by --btnmap so we can see bits in bytes
        /// the normal decode ignores. Not used on the hot path.
        /// </summary>
        public byte[] ReadRaw(int timeoutMs = 20)
        {
            byte[] buf = new byte[8];
            int xfer;
            ErrorCode ec = _reader.Read(buf, timeoutMs, out xfer);
            if (ec != ErrorCode.None || xfer < 8) return null;
            return buf;
        }

        /// <summary>Signed MPG detents since last call (handles 8-bit wrap).</summary>
        public int MpgDelta(byte mpg)
        {
            if (!_haveMpg) { _lastMpg = mpg; _haveMpg = true; return 0; }
            int d = (sbyte)(mpg - _lastMpg);
            _lastMpg = mpg;
            return d;
        }

        /// <summary>
        /// Write a two-line LCD frame. Each line is padded/truncated to 8 chars.
        /// axisMode is the byte-17 indicator (0 is safe). The activity counter
        /// (byte 18) is auto-incremented so the firmware treats each frame as new.
        /// </summary>
        public void WriteLcd(string line1, string line2, byte axisMode = 0)
        {
            byte[] f = new byte[19];
            PutLine(f, 0, line1);   // bytes 0-7  : line 1
            PutLine(f, 8, line2);   // bytes 8-15 : line 2
            f[16] = 0;
            f[17] = axisMode;       // axis/mode indicator
            f[18] = _activity++;    // activity counter (liveness)
            int xfer;
            _writer.Write(f, 30, out xfer);
        }

        private static void PutLine(byte[] f, int off, string s)
        {
            s = s ?? string.Empty;
            for (int i = 0; i < 8; i++)
                f[off + i] = (byte)(i < s.Length ? s[i] : ' ');
        }

        public void Dispose()
        {
            try
            {
                IUsbDevice whole = _dev as IUsbDevice;
                if (whole != null) whole.ReleaseInterface(0);
                if (_dev != null && _dev.IsOpen) _dev.Close();
            }
            catch { /* ignore on shutdown */ }
            UsbDevice.Exit();
        }
    }
}
