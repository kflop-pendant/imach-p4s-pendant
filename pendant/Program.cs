/*
 * Program.cs  --  iMach P4-S -> KFLOP pendant bridge, Stage 2 entry point
 *
 * Startup is PATIENT, for unattended auto-start (launched at logon, BEFORE the
 * KFLOP is powered and BEFORE an init is loaded):
 *   1. open the pendant (retry until present);
 *   2. wait for the KFLOP to answer (retry until powered / reachable);
 *   3. wait for an init to publish its config id at UserData double-index 54
 *      (1 = Standard / quill-on-ch2, 2 = HSS or Knee-Z / knee-on-ch2). That one
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
        static readonly string WatchCFile   = Path.Combine(AppDir, "EStopWatch.c");
        static readonly string PromptCFile  = Path.Combine(AppDir, "InitPrompt.c");
        static readonly string ConfigFile   = Path.Combine(AppDir, "pendant.conf");

        static volatile int    _ledByte = 0;
        static volatile bool   _ledRun  = true;
        static volatile bool   _stop    = false;
        static volatile Bridge _activeBridge;

        // Named event bridge-control.ps1 sets to ask for a clean exit (never force-kill
        // a bridge that may be mid-call to the KMotion server).
        public const string StopEventName = @"Local\iMachKflop.Stop";

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

            // Clean stop on request (bridge-control.ps1 Stop / Restart signal this event):
            // same as Ctrl+C -- the loops finish their current KFLOP call and the bridge
            // exits normally. Being force-killed in the middle of a KMotionServer exchange
            // twice left the server waiting forever on a dead client, and nothing could
            // reach the KFLOP until a full power cycle (2026-10-01, 2026-10-03).
            try
            {
                var stopEvt = new EventWaitHandle(false, EventResetMode.ManualReset, StopEventName);
                stopEvt.Reset();   // a stale signal from an earlier stop must not end this run
                new Thread(() =>
                {
                    stopEvt.WaitOne();
                    Console.WriteLine("Stop requested -- finishing the current KFLOP call and exiting.");
                    _stop = true;
                    var b = _activeBridge;
                    if (b != null) b.Stop();
                }) { IsBackground = true, Name = "StopRequest" }.Start();
            }
            catch (Exception ex) { Console.WriteLine("Stop-request event unavailable: " + ex.Message); }

            using (var pendant = new Pendant())
            {
                // The diagnostic modes need the pendant; normal runs do NOT (2026-10-03):
                // the machine services -- E-stop watch (T5), PendantService (T7), the
                // screen Machine Status button and its LOCKED/UNLOCKED label -- run with
                // or without a pendant. A missing pendant is picked up when plugged in.
                if (ledMap || btnMap)
                {
                    if (!OpenPendantPatient(pendant)) return;  // false only on Ctrl+C
                    if (ledMap) RunLedMap(pendant); else RunBtnMap(pendant);
                    return;
                }
                bool havePendant = TryOpenPendant(pendant);
                Console.WriteLine(havePendant ? "Pendant found."
                                              : "No pendant connected -- running the machine services without it; it will be picked up when plugged in.");

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

                using (var kflop = new KflopLink(ServiceCFile, WatchCFile))
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

                    BridgeExit reason = BridgeExit.Stopped;
                    bool runBridge = havePendant;
                    if (!havePendant)
                    {
                        reason = RunWithoutPendant(pendant, kflop, out runBridge);
                    }
                    if (runBridge)
                    {
                        var bridge = new Bridge(pendant, kflop);
                        _activeBridge = bridge;
                        Console.WriteLine("Running. Watch the pendant LCD for the DRO. Press Ctrl+C to stop.");

                        reason = bridge.Run();
                        _activeBridge = null;
                    }

                    if (reason == BridgeExit.LinkLost)
                    {
                        Console.WriteLine("KFLOP link lost -- exiting. Power-cycle the controller and relaunch the pendant.");
                        SafeLcd(pendant, Tune.LinkLostL1, Tune.LinkLostL2);
                    }
                    else if (reason == BridgeExit.CncRestarted)
                    {
                        // KMotionCNC closed, crashed or was restarted. Exit clean so the
                        // supervisor relaunches the bridge fresh for the next session.
                        Console.WriteLine("KMotionCNC closed or restarted -- exiting so the bridge starts fresh.");
                        SafeLcd(pendant, Tune.WaitCncL1, Tune.WaitCncL2);
                    }
                    else if (reason == BridgeExit.PendantLost)
                    {
                        // Pendant input (USB) stopped: the watchdog already stopped any
                        // jog. Exit so the supervisor relaunches us (a fresh process is
                        // the reliable way to re-open the USB device) -- but LEAVE the
                        // KFLOP programs running: the E-stop watch and the screen
                        // Machine Status button must not depend on the pendant. The
                        // relaunched bridge carries on without the pendant until it's back.
                        kflop.KeepProgramsOnDispose = true;
                        Console.WriteLine("Pendant input lost (USB) -- jog stopped; machine services keep running; relaunching to wait for the pendant.");
                    }
                    else
                    {
                        Console.WriteLine("Stopped.");
                    }
                }
            }
        }

        // One quiet attempt to open the pendant (false if it isn't plugged in / no driver).
        static bool TryOpenPendant(Pendant pendant)
        {
            try { return pendant.Open(); } catch { return false; }
        }

        // Connected to the KFLOP but no pendant: keep the machine services working --
        // PendantService (T7) and EStopWatch (T5) are already running on the KFLOP; here
        // the screen "Machine Status" button is relayed (same idle gate as F3), link health
        // and KMotionCNC restarts are watched, and the pendant is looked for once a second.
        // runBridge = true when the pendant appeared (the caller then runs the Bridge).
        static BridgeExit RunWithoutPendant(Pendant pendant, KflopLink kflop, out bool runBridge)
        {
            runBridge = false;
            string session = CncSessionKey();
            var clock = Stopwatch.StartNew();
            long nextPendantTry = 1000, nextCncCheck = 1000;
            int throws = 0;
            Console.WriteLine("Running without a pendant: screen Machine Status button, E-stop watch and LOCKED/UNLOCKED label active.");

            while (!_stop)
            {
                try
                {
                    kflop.Service();                  // idle detection + PendantService heartbeat
                    throws = 0;
                    if (kflop.ScreenTogglePending())
                    {
                        kflop.ClearScreenToggle();
                        if (kflop.MachineIdle) kflop.RequestMachineToggle();
                    }
                }
                catch { throws++; }
                if (throws >= 6 || !kflop.ServiceAlive)
                {
                    kflop.MarkLinkLost();
                    return BridgeExit.LinkLost;
                }

                long now = clock.ElapsedMilliseconds;
                if (now >= nextCncCheck)
                {
                    nextCncCheck = now + 1000;
                    if (CncSessionKey() != session) return BridgeExit.CncRestarted;
                }
                if (now >= nextPendantTry)
                {
                    nextPendantTry = now + 1000;
                    if (TryOpenPendant(pendant))
                    {
                        Console.WriteLine("Pendant connected.");
                        runBridge = true;
                        return BridgeExit.Stopped;
                    }
                }
                Thread.Sleep(50);
            }
            return BridgeExit.Stopped;
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
            string promptedFor = null;     // KMotionCNC session InitPrompt.c was started for (this KFLOP power-up)

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
                    promptedFor = null;       // a (re)appearing board may have been power-cycled
                }

                bool cnc = CncRunning();
                int  id  = kflop.ReadConfigId();

                if (cnc && (id == 1 || id == 2))
                {
                    PromptIfNewCncSession(kflop);
                    return id;
                }

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
                    // Blink "CHOOSE init ->" on the screen while nothing is loaded. Only
                    // when no init has even STARTED since power-up (var 58 == 0) -- a
                    // negative value means one is loading, and the init owns the label.
                    // Once per KMotionCNC session (and per power-up): InitPrompt.c runs
                    // until an init starts, but KMotionCNC can be closed and reopened
                    // while we wait here, and the new session must blink too (2026-10-03).
                    // Failure is logged only.
                    string session = CncSessionKey();
                    if (session != null && session != promptedFor && kflop.ReadInitState() == 0)
                    {
                        promptedFor = session;
                        RememberCncSession(session);   // this session has been prompted
                        string err = kflop.LaunchPrompt(PromptCFile);
                        Console.WriteLine(err == null ? "No init loaded -- screen prompt \"CHOOSE init ->\" blinking."
                                                      : "Screen init prompt not started: " + err);
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
            try
            {
                foreach (string name in Tune.CncProcessNames)
                    if (Process.GetProcessesByName(name).Length > 0) return true;
                return false;
            }
            catch { return false; }
        }

        // ---- "CHOOSE init ->" for a NEW KMotionCNC session ------------------------
        // Closing and reopening KMotionCNC leaves the init running on the KFLOP (var
        // 58 > 0), but the new KMotionCNC has lost the init's settings (Z/C scale,
        // TP velocities -- the RELOAD INIT case), so the init has to be loaded again.
        // Once per KMotionCNC session (process id + start time, remembered in a file
        // so a bridge restart within the same session doesn't prompt again) start the
        // screen blinker; InitPrompt.c runs until an init load starts.
        static readonly string CncSessionFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PendantBridge", "cnc-session.txt");

        static void PromptIfNewCncSession(KflopLink kflop)
        {
            string key = CncSessionKey();
            if (key == null || key == LastPromptedCncSession()) return;
            RememberCncSession(key);
            if (kflop.ReadInitState() <= 0) return;   // none / loading: the wait loop or the init owns the label
            string err = kflop.LaunchPrompt(PromptCFile);
            Console.WriteLine(err == null ? "New KMotionCNC session with an init still on the KFLOP -- screen prompt \"CHOOSE init ->\" blinking."
                                          : "Screen init prompt not started: " + err);
        }

        // "<pid>@<start time>" of the running KMotionCNC (lowest pid), null if none.
        static string CncSessionKey()
        {
            try
            {
                Process best = null;
                foreach (string name in Tune.CncProcessNames)
                    foreach (var p in Process.GetProcessesByName(name))
                    {
                        if (best == null || p.Id < best.Id) { if (best != null) best.Dispose(); best = p; }
                        else p.Dispose();
                    }
                if (best == null) return null;
                using (best) return best.Id + "@" + best.StartTime.ToUniversalTime().Ticks;
            }
            catch { return null; }
        }

        static string LastPromptedCncSession()
        {
            try { return File.Exists(CncSessionFile) ? File.ReadAllText(CncSessionFile).Trim() : null; }
            catch { return null; }
        }

        static void RememberCncSession(string key)
        {
            if (key == null) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CncSessionFile));
                File.WriteAllText(CncSessionFile, key);
            }
            catch { }
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
