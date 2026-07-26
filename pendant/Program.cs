/*
 * Program.cs  --  iMach P4-S -> KFLOP pendant bridge, Stage 2 entry point
 *
 * Startup is PATIENT, for unattended auto-start (launched at logon, BEFORE the
 * KFLOP is powered and BEFORE an init is loaded):
 *   1. open the pendant (retry until present);
 *   2. wait for the KFLOP to answer (retry until powered / reachable);
 *   3. wait for an init to publish its config id at UserData double-index 54
 *      (1 = Standard / quill-on-ch2, 2 = PCB or Knee-Z / knee-on-ch2). That one
 *      value is BOTH the "init has run" signal AND the knee/quill selector, so a
 *      single build is correct for whichever init you load. It is NOT cleared:
 *      the inits run a forever loop, so 54 always reflects the live config.
 *   4. Connect() (loads PendantService.c on thread 7, AFTER init) and run.
 *
 * SINGLE-SHOT + FAIL-SAFE: run the gate->connect->run cycle ONCE. If the KFLOP
 * link is lost mid-session, Bridge.Run() detects it (stale heartbeat or throwing
 * board ops), stops touching the board, and returns; we print and EXIT cleanly.
 * We deliberately do NOT re-arm in-process -- re-polling a suddenly-dead board
 * can wedge KMotionServer (and thus KMotionCNC). Auto-recovery after an abnormal
 * power event is left to Task Scheduler restarting the exe on exit (3b).
 * The LCD shows WAIT / KFLOP, WAIT / CNC, then WAIT / INIT so you can see it is
 * alive. It only connects once KMotionCNC is running AND an init is loaded, so it
 * waits quietly (no flapping) when the PC is used without a milling session.
 *
 * "--ledmap": opens ONLY the pendant to sweep the byte-17 LCD indicator by hand.
 * "--btnmap": opens ONLY the pendant and traces the raw input report so you can
 *             map which bits each button (and each half of the split buttons)
 *             sends. No KFLOP / machine power needed.
 */

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace iMachKflop
{
    static class Program
    {
        // PendantService.c (the KFLOP-side program) and pendant.conf (the speed/feel
        // config) are loaded from the SAME directory as this exe -- the build deploys
        // both there (see iMachKflop.csproj). This keeps the bridge portable: it works
        // from any checkout / any KMotion install with no hardcoded machine paths. Edit
        // pendant.conf in place next to the exe and restart -- no rebuild (the build
        // seeds it only if absent, so your edits survive rebuilds).
        static readonly string AppDir      = AppDomain.CurrentDomain.BaseDirectory;
        static readonly string ServiceCFile = Path.Combine(AppDir, "PendantService.c");
        static readonly string ConfigFile   = Path.Combine(AppDir, "pendant.conf");

        static volatile int    _ledByte = 0;
        static volatile bool   _ledRun  = true;
        static volatile bool   _stop    = false;
        static volatile Bridge _activeBridge;

        static void Main(string[] args)
        {
            bool ledMap = args != null && args.Length > 0 &&
                          (args[0] == "--ledmap" || args[0] == "-l");
            bool btnMap = args != null && args.Length > 0 &&
                          (args[0] == "--btnmap" || args[0] == "-b");

            // One Ctrl+C handler: stop waiting and stop the active bridge (if any).
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                _stop = true;
                var b = _activeBridge;
                if (b != null) b.Stop();
            };

            using (var pendant = new Pendant())
            {
                if (!OpenPendantPatient(pendant)) return;      // false only on Ctrl+C

                if (ledMap) { RunLedMap(pendant); return; }
                if (btnMap) { RunBtnMap(pendant); return; }

                // Load the external speed/feel config (pendant.conf) BEFORE any KFLOP
                // work. SAFETY: a missing/malformed/out-of-range config makes us refuse
                // to start rather than guess a speed -- CONFIG/ERR on the LCD, full
                // detail on the console. Diagnostic modes above skip this so pendant
                // bring-up works without a valid config. (Task Scheduler will relaunch
                // us; the banner stays up until the file is fixed.)
                try
                {
                    TuneConfig.Load(ConfigFile);
                }
                catch (ConfigException ex)
                {
                    Console.WriteLine();
                    Console.WriteLine("CONFIG ERROR -- refusing to start:");
                    Console.WriteLine("  " + ex.Message);
                    Console.WriteLine();
                    Console.WriteLine("Fix " + ConfigFile + " and restart (bridge-control.ps1 -Action Restart).");
                    SafeLcd(pendant, Tune.ConfigErrL1, Tune.ConfigErrL2);
                    return;
                }

                using (var kflop = new KflopLink(ServiceCFile))
                {
                    int configId = WaitForKflopAndInit(pendant, kflop);
                    if (configId == 0) return;                 // Ctrl+C during the wait

                    try
                    {
                        Console.WriteLine("Init detected (config id " + configId +
                                          "). Connecting and starting service thread...");
                        kflop.Connect(configId);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("KFLOP connect failed: " + ex.Message);
                        Console.WriteLine("  - Did PendantService.c compile? (include path / thread conflict)");
                        Console.WriteLine("  - Is KMotionCNC still open (watchdog + drives live)?");
                        SafeLcd(pendant, Tune.ConnFailL1, Tune.ConnFailL2);
                        return;
                    }

                    var bridge = new Bridge(pendant, kflop);
                    _activeBridge = bridge;
                    Console.WriteLine("Running. Watch the pendant LCD for the DRO. Press Ctrl+C to stop.");

                    BridgeExit reason = bridge.Run();
                    _activeBridge = null;

                    if (reason == BridgeExit.LinkLost)
                    {
                        Console.WriteLine("KFLOP link lost -- exiting. Power-cycle the controller and relaunch the pendant.");
                        SafeLcd(pendant, Tune.LinkLostL1, Tune.LinkLostL2);
                    }
                    else if (reason == BridgeExit.PendantLost)
                    {
                        // Pendant input (USB) stopped: the watchdog already stopped any
                        // jog. Exit clean so Task Scheduler relaunches and re-opens the
                        // pendant. No LCD write -- the pendant is gone.
                        Console.WriteLine("Pendant input lost (USB) -- jog stopped, exiting to relaunch and re-open the pendant.");
                    }
                    else
                    {
                        Console.WriteLine("Stopped.");
                    }
                }
            }
        }

        // Retry pendant.Open() until it succeeds or Ctrl+C. Returns false only on _stop.
        static bool OpenPendantPatient(Pendant pendant)
        {
            bool announced = false;
            while (!_stop)
            {
                if (pendant.Open()) return true;
                if (!announced)
                {
                    Console.WriteLine("Waiting for pendant (USB / WinUSB via Zadig)...");
                    announced = true;
                }
                Thread.Sleep(Tune.GatePendantRetryMs);
            }
            return false;
        }

        // Wait until the machine is actually ready, then return the init config id.
        //   1. KFLOP answers (powered / reachable),
        //   2. KMotionCNC is running (needed for DROs + drive-enable; without it a
        //      connect just fails), and
        //   3. an init has published a config id (var 54 = 1 or 2).
        // We require KMotionCNC to be running BEFORE connecting so the bridge sits
        // quietly at WAIT/CNC when the PC is used without a milling session, instead
        // of connecting on a leftover config id, failing, and relaunch-flapping.
        // Returns the id, or 0 if Ctrl+C was pressed.
        static int WaitForKflopAndInit(Pendant pendant, KflopLink kflop)
        {
            bool sawBoard = false;
            bool announcedKflop = false, announcedCnc = false, announcedInit = false;

            while (!_stop)
            {
                if (!kflop.BoardResponds())
                {
                    sawBoard = false;
                    if (!announcedKflop)
                    {
                        Console.WriteLine("Waiting for KFLOP (power up the controller)...");
                        announcedKflop = true;
                    }
                    // Leave the LCD alone here so the pendant firmware's "LinuxCNC"
                    // idle screen stays up until the KFLOP is present (then WaitCnc).
                    Thread.Sleep(Tune.GateKflopPollMs);
                    continue;
                }

                if (!sawBoard)
                {
                    Console.WriteLine("KFLOP present.");
                    sawBoard = true;
                    announcedKflop = false;
                    announcedCnc = false;
                    announcedInit = false;
                }

                bool cnc = CncRunning();
                int  id  = kflop.ReadConfigId();

                if (cnc && (id == 1 || id == 2)) return id;

                if (!cnc)
                {
                    if (!announcedCnc)
                    {
                        Console.WriteLine("Waiting for KMotionCNC to be running...");
                        announcedCnc = true;
                        announcedInit = false;
                    }
                    SafeLcd(pendant, Tune.WaitCncL1, Tune.WaitCncL2);
                }
                else
                {
                    if (!announcedInit)
                    {
                        Console.WriteLine("KMotionCNC up. Waiting for an init (press your init button)...");
                        announcedInit = true;
                        announcedCnc = false;
                    }
                    SafeLcd(pendant, Tune.WaitInitL1, Tune.WaitInitL2);
                }
                Thread.Sleep(Tune.GateConfigPollMs);
            }
            return 0;
        }

        // Is KMotionCNC actually running? (process name from Tune, no .exe)
        static bool CncRunning()
        {
            try { return Process.GetProcessesByName(Tune.CncProcessName).Length > 0; }
            catch { return false; }
        }

        static void SafeLcd(Pendant pendant, string l1, string l2)
        {
            try { pendant.WriteLcd(l1, l2, 0); } catch { }
        }

        static void RunLedMap(Pendant pendant)
        {
            Console.WriteLine();
            Console.WriteLine("=== LED MAP MODE (byte17 sweep) ===");
            Console.WriteLine("Commands:  [Enter] = +1    -  = -1    <number> = set    q = quit");
            Console.WriteLine();

            var refresher = new Thread(() =>
            {
                while (_ledRun)
                {
                    try { pendant.WriteLcd("LED MAP", "B17=" + _ledByte.ToString("D3"), (byte)_ledByte); }
                    catch { }
                    Thread.Sleep(500);
                }
            });
            refresher.IsBackground = true;
            refresher.Start();

            while (true)
            {
                Console.Write("byte17 = " + _ledByte + "  > ");
                string line = Console.ReadLine();
                if (line == null) break;
                line = line.Trim();

                if (line == "q" || line == "Q") break;
                else if (line == "-")            _ledByte = (_ledByte + 255) & 0xFF;
                else if (line.Length == 0)       _ledByte = (_ledByte + 1)   & 0xFF;
                else if (int.TryParse(line, out int j)) _ledByte = j & 0xFF;
                else Console.WriteLine("  (didn't understand; use Enter / - / a number / q)");
            }

            _ledRun = false;
            Console.WriteLine("LED map mode ended.");
        }

        // Raw input tracer for mapping the buttons (incl. the split-button halves).
        // Opens ONLY the pendant -- no KFLOP / machine power needed. Prints the full
        // 8-byte report + decoded Btn/Mode bits whenever the BUTTON bytes change.
        // The MPG wheel (byte 0) and the 0.5s blink bit (byte 3, 0x08) are ignored
        // for change-detection so they don't spam, but they still show in the hex.
        static void RunBtnMap(Pendant pendant)
        {
            Console.WriteLine();
            Console.WriteLine("=== BUTTON MAP MODE (raw input trace) ===");
            Console.WriteLine("Prints  b0 b1 b2 b3 b4 b5 b6 b7  + decoded Btn(b2)/Mode(b3) on each change.");
            Console.WriteLine("(MPG wheel b0 and the 0.5s blink bit b3&0x08 are ignored so they don't spam.)");
            Console.WriteLine("Press each button, and each HALF of the split buttons; note tap vs hold.");
            Console.WriteLine("If you press something and NOTHING prints, tell me -- that button uses a");
            Console.WriteLine("byte I'm not watching for changes yet.  Press Ctrl+C to quit.");
            Console.WriteLine();

            byte[] prev = null;
            while (!_stop)
            {
                byte[] buf = pendant.ReadRaw(20);
                if (buf == null) continue;
                if (!BtnBytesChanged(buf, prev)) continue;
                prev = buf;

                string hex = "";
                for (int i = 0; i < 8; i++) hex += buf[i].ToString("X2") + " ";

                string btn  = DecodeBtn(buf[2]);
                string mode = DecodeMode(buf[3]);
                Console.WriteLine(hex.Trim()
                    + "  |  Btn: "  + (btn.Length  == 0 ? "-" : btn)
                    + "  |  Mode: " + (mode.Length == 0 ? "-" : mode));
            }
            Console.WriteLine("Button map mode ended.");
        }

        // Change only counts if byte 2 (Btn) or byte 3 (Mode, minus the blink bit)
        // differs. Byte 0 (wheel) and byte-3 bit 0x08 (blink) are ignored.
        static bool BtnBytesChanged(byte[] cur, byte[] prev)
        {
            if (prev == null) return true;
            if (cur[2] != prev[2]) return true;
            if ((cur[3] & 0xF7) != (prev[3] & 0xF7)) return true;   // 0xF7 = ~0x08
            return false;
        }

        static string DecodeBtn(byte b)
        {
            string s = "";
            if ((b & 0x01) != 0) s += "EStop ";
            if ((b & 0x02) != 0) s += "Spindle ";
            if ((b & 0x04) != 0) s += "Start ";
            if ((b & 0x08) != 0) s += "Stop ";
            if ((b & 0x10) != 0) s += "En ";
            if ((b & 0x20) != 0) s += "AxXA ";
            if ((b & 0x40) != 0) s += "AxYB ";
            if ((b & 0x80) != 0) s += "AxZC ";
            return s.Trim();
        }

        static string DecodeMode(byte b)
        {
            string s = "";
            if ((b & 0x01) != 0) s += "ModeStep ";
            if ((b & 0x02) != 0) s += "ModeCont ";
            if ((b & 0x04) != 0) s += "ModeF ";
            if ((b & 0x08) != 0) s += "blink ";
            if ((b & 0x10) != 0) s += "Zplus ";
            if ((b & 0x20) != 0) s += "XYplus ";
            if ((b & 0x40) != 0) s += "XYminus ";     // F3 -- hardware-confirmed
            if ((b & 0x80) != 0) s += "bit0x80?? ";   // unused on the P4-S; watch it on other models
            return s.Trim();
        }
    }
}
