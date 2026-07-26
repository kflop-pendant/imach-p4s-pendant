/*
 * KflopLink.cs  --  iMach P4-S -> KFLOP pendant bridge, Stage 2
 *
 * KFLOP/KMotionCNC side of the bridge. Coexists with a running KMotionCNC.
 * Jogs axes, publishes DROs (via PendantService.c), zeroes the work offset,
 * relays interpreter commands, and interlocks against other motion.
 *
 * USER-TUNABLE speed/feel values live in Tune.cs. This file holds plumbing.
 *
 * TWO INDEX SPACES: JOG = KFLOP CHANNEL (GetAxis) ; DRO = COORDINATE slot. A
 * selection carries both. Which physical axis is on ch2/ch3 is chosen at RUNTIME
 * from the init's config id (UserData double-index 54: 1 = Standard/quill-on-ch2,
 * 2 = PCB or Knee-Z/knee-on-ch2); Tune.KneeOnCh2 is only a fallback.
 * UNITS: the raw KFLOP console commands (JogAtAccel / MoveRelAtVelAccel) operate
 * in COUNTS, so this file converts: counts/sec = ipm/60 * Cpi, counts = inches *
 * Cpi; acceleration is Tune's raw counts value passed straight through.
 * PER-COMMAND DYNAMICS (Tom Kerekes 2026-07-25): velocity + accel travel WITH each
 * jog/step command; Jerk stays the init's channel setting. The bridge NEVER writes
 * ch->Vel/Accel/Jerk, so it can't leave a residue that degrades a later rapid --
 * fixing the cold-start race and the slow-rapids-after-step-jog bug by construction.
 *
 * STARTUP HELPERS for unattended auto-start: BoardResponds() and ReadConfigId()
 * are safe to call before Connect() and when the KFLOP is unpowered/absent
 * (they return false/0 rather than throwing). The controller is constructed
 * lazily so a board-absent launch can't crash the process.
 *
 * UserData layout -- MUST MATCH PendantService.c (double-index):
 *   30..35 TMP scratch | 36..49 data block | 50..53 PC_COMM | 54 config id (init->bridge) | 55 spindle (service->bridge)
 *   56/57 TP_HOLD_REQ/ACK (init<->service handshake) | 58 init identity (init->bridge) | 62..64 GOTOZ | 65 machine | 66 screen-toggle
 */

using System;
using System.Globalization;
using System.IO;
using System.Threading;
using KMotion_dotNet;

namespace iMachKflop
{
    public enum ZeroState { Idle, Pending, Ok, BadAxis, Timeout }

    public sealed class KflopLink : IDisposable
    {
        const int UD_HEARTBEAT = 36;
        const int UD_DRO       = 39;   // 39..44 (X..C)
        const int UD_FIXTURE   = 46;
        const int UD_CMD_REQ   = 47;
        const int UD_CMD_ARG   = 48;
        const int UD_CMD_STAT  = 49;
        const int UD_CONFIG    = 54;   // init publishes 1 (Standard) or 2 (PCB/Knee-Z) -- knee/quill resolver
        const int UD_SPINDLE   = 55;   // PendantService publishes live spindle enable (ReadBit 156)
        const int UD_INIT_ID   = 58;   // init publishes its identity for the LCD banner (1 Std / 2 Knee Z / 3 PCB).
                                       // NOT 56/57 -- those are the init<->service TP_HOLD_REQ/ACK handshake.
        const int UD_GOTOZ_CLEAR = 62; // GOTOZ params (bridge -> service, written once at connect)
        const int UD_GOTOZ_IPMZ  = 63;
        const int UD_GOTOZ_IPMXY = 64;
        const int UD_MACHINE     = 65; // live machine (axes) enable, published by the service
        const int UD_SCREEN_TOGGLE = 66; // screen "Machine Status" button -> bridge: 1 = toggle requested

        const int CMD_NONE = 0, CMD_EXECUTE = 1, CMD_ESTOP = 2,
                  CMD_HALT = 3, CMD_MCODE = 4, CMD_FRO_INC = 5, CMD_SPINDLE = 6, CMD_SSO = 7, CMD_ZERO = 8, CMD_GOTOZ = 9, CMD_MACHINE = 10;

        const int NumChan = 6;
        const int ServiceThread = 7;

        const int    StatusIntervalMs    = 50;
        const double DestEpsilonCounts   = 3.0;
        const int    IdleSamplesRequired = 5;
        const int    MotionTailMs        = 250;
        const int    HeartbeatStallLimit = 10;
        const int    ServiceStartMs      = 2000;
        // Minimum spacing between zeros (fire-and-forget via CMD_ZERO). Blocks a
        // second zero for this long so rapid taps can't flood KMotionCNC.
        const int    ZeroGuardMs         = 300;
        // Delay before a dispatched zero is optimistically marked done (LCD 'ZEROED').
        const int    ZeroConfirmMs       = 150;
        // GOTOZ sequencing: gate the X/Y phase on Z reaching clearance (position,
        // not motion -- so HOLD/E-stop mid-retract can't fire X/Y). PosEps = "close
        // enough" in inches; MaxMs unsticks the sequence if it never completes.
        const double GotozPosEps         = 0.002;
        const int    GotozMaxMs          = 30000;

        // E-STOP DECEL ACCEL (counts/sec^2) for the belt-and-suspenders
        // StopAllChannels() path. SAFETY DECISION -- Jim to confirm the value.
        // Under per-command dynamics the bridge no longer leaves a large accel on
        // the channel, so JogAtAccelN=0 must STATE its decel. Set >= the largest
        // init channel accel (X/Y Accel = 3,053,400) so a software E-stop
        // decelerates AT LEAST as hard as before on every axis. Over-decelerating a
        // gentle axis (knee ~100k) is harmless for an emergency stop. The HARDWARE
        // E-stop (init DisableAxis on bit 143) remains the primary stop regardless.
        const double EStopDecelCounts = 4000000.0;

        // Axis ids -- indices into Tune.Axes[] and _sel[], in PAIR order so the
        // three select buttons map cleanly: [AxX|AxA], [AxY|AxB], [AxZ|AxC].
        public const int AxX = 0, AxA = 1, AxY = 2, AxB = 3, AxZ = 4, AxC = 5;
        const int NumAxes = 6;

        sealed class Sel
        {
            public bool   Enabled;
            public bool   Rotary;
            public int    JogCh;
            public int    DroSlot;
            public double Cpi;            // counts per inch OR per degree (rotary)
            public double MaxIpm;         // IPM OR deg/min (rotary)
            public double JogAccelCounts;
            public double StepAccelCounts;
            public char   Label;
        }

        // GetAxis display names, indexed by CHANNEL (ch0=X ch1=Y ch2=Z ch3=C ch4=A ch5=B).
        static readonly string[] Names = { "X", "Y", "Z", "C", "A", "B" };

        KM_Controller      _km;                    // lazily constructed (EnsureController)
        readonly KM_Axis[] _axis = new KM_Axis[NumChan];
        readonly string    _serviceCFile;
        readonly Sel[]     _sel  = new Sel[NumAxes];

        bool _kneeOnCh2 = Tune.KneeOnCh2;          // set from the runtime config id in Connect()
        int  _configId;                            // init config id in effect (1 Standard / 2 Knee-Z); re-read mid-session
        int  _initId;                              // init IDENTITY in effect (1 Std / 2 Knee Z / 3 PCB); for the LCD banner
        bool _primed;                              // StatusUpdateInterval applied once

        readonly double[] _lastDest = new double[NumChan];
        int  _idleSamples;
        long _lastBridgeMotionMs;

        double _lastHeartbeat = double.NaN;
        int    _heartbeatStalls;

        ZeroState _zeroState = ZeroState.Idle;
        long      _zeroStartMs;
        int       _lastSentAxis = -1;   // coord axis the bridge last dispatched via ZERO_REQ
        ZeroState _gotozState = ZeroState.Idle;   // GOTOZ (F2) reuses the same state enum
        long      _gotozStartMs;
        int       _gotozPhase;                    // 0 idle, 1 retracting Z, 2 moving X/Y


        public bool Connected    { get; private set; }
        public bool ServiceAlive => _heartbeatStalls < HeartbeatStallLimit;
        public bool MachineIdle  => _idleSamples >= IdleSamplesRequired;

        public KflopLink(string serviceCFile)
        {
            _serviceCFile = serviceCFile;
        }

        void EnsureController()
        {
            if (_km == null) _km = new KM_Controller(true);
        }

        // ---- startup helpers (safe when the board is absent) -------------------

        // True iff the KFLOP answered a trivial read this call. Returns false
        // (does not throw) when the board is unpowered / unreachable.
        public bool BoardResponds()
        {
            try
            {
                EnsureController();
                if (!_primed) { _km.StatusUpdateInterval = StatusIntervalMs; _primed = true; }
                _km.GetUserDataDouble(UD_HEARTBEAT);
                return true;
            }
            catch { _primed = false; return false; }
        }

        // The init-published config id at UserData double-index 54.
        // 1 = Standard (quill on ch2), 2 = PCB / Knee-Z (knee on ch2), 0 = not set / unreadable.
        public int ReadConfigId()
        {
            try
            {
                EnsureController();
                int id = (int)Math.Round(_km.GetUserDataDouble(UD_CONFIG));
                return (id == 1 || id == 2) ? id : 0;
            }
            catch { return 0; }
        }

        // The init-published IDENTITY at UserData double-index 58 (for the LCD banner):
        // 1 = Standard, 2 = Knee Z, 3 = PCB, 0 = not set / unreadable (init without var 58).
        // Distinct per init, so it changes on EVERY init load -- including Knee Z <-> PCB,
        // which share config id 2 and so are indistinguishable from var 54 alone. (Index 58,
        // not 56: 56/57 are the init<->service TP_HOLD handshake, which briefly sets 56=1
        // mid-load -- reading identity there mis-fired a spurious "Standard" banner.)
        public int ReadInitId()
        {
            try
            {
                EnsureController();
                int id = (int)Math.Round(_km.GetUserDataDouble(UD_INIT_ID));
                return (id == 1 || id == 2 || id == 3) ? id : 0;
            }
            catch { return 0; }
        }

        // Called when the link is known lost so teardown does NOT poke a dead
        // board (Dispose skips KillProgramThreads when !Connected). Cheap, no I/O.
        public void MarkLinkLost() { Connected = false; }

        // ---- connect / bring-up ------------------------------------------------

        public void Connect(int configId)
        {
            // Runtime knee/quill selection from the init's config id; fall back to
            // the compile-time default only if the id is somehow unknown.
            _kneeOnCh2 = (configId == 1) ? false
                       : (configId == 2) ? true
                       : Tune.KneeOnCh2;
            _configId  = configId;

            EnsureController();
            _km.StatusUpdateInterval = StatusIntervalMs;
            _initId = ReadInitId();                    // init identity for the LCD banner (0 if not published)

            for (int ch = 0; ch < NumChan; ch++)
                _axis[ch] = _km.GetAxis(ch, Names[ch]);

            BuildSelections();
            ApplyAxisCpu();

            var st = _km.CurrentStatus;
            for (int ch = 0; ch < NumChan; ch++)
                _lastDest[ch] = st.GetDestination(ch);

            LaunchService();
            Connected = true;
        }

        void BuildSelections()
        {
            // Base rows straight from the user's Tune table (X, Y, A, B literal).
            for (int i = 0; i < NumAxes; i++)
            {
                var a = Tune.Axes[i];
                _sel[i] = new Sel
                {
                    Enabled         = a.Enabled,
                    Rotary          = a.Rotary,
                    JogCh           = a.JogCh,
                    DroSlot         = a.DroSlot,
                    Cpi             = a.Cpu,
                    MaxIpm          = a.MaxRate,
                    JogAccelCounts  = a.JogAccel,
                    StepAccelCounts = a.StepAccel,
                    Label           = a.Label,
                };
            }

            // Vertical pair (ch2/ch3 = Z/C): which PHYSICAL axis (knee vs quill)
            // sits there is chosen at RUNTIME from the init's config id, so the
            // Tune rows leave Cpu=0 as a "resolve me" marker. Fill them from the
            // knee/quill scalars exactly as before -- behavior-identical to the old
            // BuildSelections. (Machines with no knee/quill swap can instead put
            // real values in the Z/C rows; then Cpu!=0 and this block is skipped.)
            if (_sel[AxZ].Cpi == 0.0)
            {
                if (_kneeOnCh2)   // PCB / Knee-Z: knee on ch2 (Z), quill on ch3 (C)
                {
                    ApplyVertical(AxZ, Tune.CpiKnee,  Tune.IpmKnee,  Tune.JogAccKnee,  Tune.StepAccKnee);
                    ApplyVertical(AxC, Tune.CpiQuill, Tune.IpmQuill, Tune.JogAccQuill, Tune.StepAccQuill);
                }
                else              // Standard: quill on ch2 (Z), knee on ch3 (C)
                {
                    ApplyVertical(AxZ, Tune.CpiQuill, Tune.IpmQuill, Tune.JogAccQuill, Tune.StepAccQuill);
                    ApplyVertical(AxC, Tune.CpiKnee,  Tune.IpmKnee,  Tune.JogAccKnee,  Tune.StepAccKnee);
                }
            }
        }

        void ApplyVertical(int ax, double cpi, double ipm, double jogAcc, double stepAcc)
        {
            _sel[ax].Cpi             = cpi;
            _sel[ax].MaxIpm          = ipm;
            _sel[ax].JogAccelCounts  = jogAcc;
            _sel[ax].StepAccelCounts = stepAcc;
        }

        // Push each enabled selection's CPU onto its KFLOP channel object.
        // PER-COMMAND DYNAMICS refactor (Tom Kerekes 2026-07-25): the bridge NO
        // LONGER writes channel Vel/Accel/Jerk. Jog velocity + accel now travel WITH
        // each JogAtAccel / MoveRelAtVelAccel console command; Jerk stays the init's
        // channel setting. This removes the cold-start race AND the slow-rapids-
        // after-step-jog bug by construction -- the bridge never mutates a channel
        // value a later rapid depends on. CPU is kept only so the axis objects report
        // status in user units; motion no longer flows through them. (Factored out so
        // a mid-session config-id change can re-apply it via ApplyConfigId.)
        void ApplyAxisCpu()
        {
            double[] cpuByCh = new double[NumChan];
            for (int i = 0; i < NumChan; i++) cpuByCh[i] = 1.0;
            for (int i = 0; i < NumAxes; i++)
                if (_sel[i].Enabled) cpuByCh[_sel[i].JogCh] = _sel[i].Cpi;
            for (int ch = 0; ch < NumChan; ch++)
                _axis[ch].CPU = cpuByCh[ch];
        }

        // The init config id currently in effect (1 Standard / 2 Knee-Z).
        public int ConfigId => _configId;

        // The init identity currently in effect (1 Standard / 2 Knee Z / 3 PCB; 0 unknown).
        public int InitId => _initId;

        // Apply a mid-session init change: record the new IDENTITY (var 56) and re-resolve
        // knee/quill from the current config id (var 54). Returns true iff the identity
        // actually changed (drives the LCD banner). The knee/quill re-resolve is a no-op
        // for a Knee Z <-> PCB swap (both config id 2) -- correct, only the name changes.
        // Rejects an invalid/unknown identity. Caller ensures it's safe (idle, not jogging).
        public bool ApplyInit(int initId, int configId)
        {
            if (initId != 1 && initId != 2 && initId != 3) return false;
            if (initId == _initId) return false;
            _initId = initId;
            ApplyConfigId(configId);   // re-resolve knee/quill (no-op when it didn't change)
            return true;
        }

        // Re-resolve knee/quill for a NEW init config id WITHOUT relaunching the
        // service (it is already running). Rebuilds the Z/C selections and re-applies
        // channel CPU; returns true iff the resolution actually changed. Rejects an
        // invalid/unknown id (only 1 or 2 are valid). The CALLER must ensure this is
        // safe -- Bridge gates it on machine-idle + not-jogging so axis params never
        // change under an active jog. Runs on the bridge's single loop thread, same
        // as the jog handling, so there is no cross-thread race on _sel[].
        public bool ApplyConfigId(int configId)
        {
            if (configId != 1 && configId != 2) return false;   // ignore transient/unknown
            if (configId == _configId)          return false;   // no change
            _configId  = configId;
            _kneeOnCh2 = (configId == 2);                        // 1 Standard -> false, 2 Knee-Z -> true
            BuildSelections();
            ApplyAxisCpu();
            return true;
        }

        void LaunchService()
        {
            if (!File.Exists(_serviceCFile))
                throw new FileNotFoundException("PendantService.c not found", _serviceCFile);

            _km.SetUserDataDouble(UD_CMD_REQ, CMD_NONE);
            _km.SetUserDataDouble(UD_CMD_STAT, 0.0);
            _km.SetUserDataDouble(UD_HEARTBEAT, 0.0);


            // GOTOZ (F2) parameters -> the service reads these when it runs the move.
            _km.SetUserDataDouble(UD_GOTOZ_CLEAR, Tune.GotozZClearInch);
            _km.SetUserDataDouble(UD_GOTOZ_IPMZ,  Tune.GotozIpmZ);
            _km.SetUserDataDouble(UD_GOTOZ_IPMXY, Tune.GotozIpmXY);

            string err = _km.ExecuteProgram(ServiceThread, _serviceCFile, false);
            if (!string.IsNullOrEmpty(err))
                throw new InvalidOperationException("PendantService load failed: " + err);

            double h0 = _km.GetUserDataDouble(UD_HEARTBEAT);
            long t0 = Environment.TickCount;
            while (Environment.TickCount - t0 < ServiceStartMs)
            {
                Thread.Sleep(50);
                double hb = _km.GetUserDataDouble(UD_HEARTBEAT);
                if (hb != h0) { _lastHeartbeat = hb; return; }
            }
            throw new InvalidOperationException("PendantService did not start (no heartbeat).");
        }

        // ---- selection accessors ----------------------------------------------

        public int    DroSlot(int sel)  => _sel[sel].DroSlot;
        public char   SelLabel(int sel) => _sel[sel].Label;
        public double SelMaxIpm(int sel)=> _sel[sel].MaxIpm;
        public int    SelJogCh(int sel) => _sel[sel].JogCh;
        public bool   SelEnabled(int sel)=> _sel[sel].Enabled;
        public bool   SelRotary(int sel) => _sel[sel].Rotary;

        // First enabled axis id (for the initial selection). Falls back to AxX.
        public int FirstEnabledAxis()
        {
            for (int i = 0; i < NumAxes; i++) if (_sel[i].Enabled) return i;
            return AxX;
        }

        // ---- readback ----------------------------------------------------------

        public void SnapshotWorkDros(double[] dst)
        {
            for (int i = 0; i < 6; i++)
                dst[i] = _km.GetUserDataDouble(UD_DRO + i);
        }

        public double MachineCounts(int ch) => _km.CurrentStatus.GetDestination(ch);
        public int    ActiveFixture()        => (int)_km.GetUserDataDouble(UD_FIXTURE);
        public bool   SpindleOn()            => _km.GetUserDataDouble(UD_SPINDLE) != 0.0;
        public bool   AxisEnabled(int ch)    => _km.CurrentStatus.GetAxisEnabled(ch) != 0;

        // ---- jog (by SELECTION) ------------------------------------------------

        bool CanJogCh(int ch) => Connected && ServiceAlive && AxisEnabled(ch) && MachineIdle;
        public bool CanJogSel(int sel)     => CanJogCh(_sel[sel].JogCh);
        public bool AxisEnabledSel(int sel)=> AxisEnabled(_sel[sel].JogCh);

        // Format a double for a KFLOP console command: invariant '.' decimal and no
        // scientific notation, so a European locale can never emit a comma decimal
        // separator that KFLOP would misparse. Irrelevant on a US machine; correct
        // everywhere. All console-command numbers go through here.
        static string N(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

        // Refresh the "this motion is ours" timestamp WITHOUT issuing a command.
        // Called every active continuous-jog tick so a STEADY jog -- whose velocity
        // command is deduped and therefore not re-sent -- still counts as bridge-owned
        // motion. Without this, at a sustained (e.g. max-speed) jog the command stream
        // stops, _lastBridgeMotionMs goes stale, Service() reclassifies the ongoing
        // motion as EXTERNAL, MachineIdle flips false, and CanJogCh then blocks the
        // next velocity change -- INCLUDING a direction reversal. Root-caused from a
        // [JOGDBG] trace 2026-07-25 (wr flipped negative correctly but cmdIpm stayed
        // pinned at +max because CanJogCh was false once MachineIdle went to 0).
        public void MarkJogActive() { _lastBridgeMotionMs = Environment.TickCount; }

        // Raw console jog: velocity + accel travel WITH the command (JogAtAccelN=V A).
        // V and A are in COUNTS (the console operates on raw channel units), so IPM ->
        // counts/sec = ipm/60 * Cpi; accel is Tune's raw counts value passed straight
        // through. Jerk stays the init's channel setting. Non-blocking (WriteLine).
        public bool JogContinuousIpm(int sel, double desiredIpm)
        {
            var s = _sel[sel];
            if (!CanJogCh(s.JogCh)) return false;
            double ipm       = Math.Max(-s.MaxIpm, Math.Min(s.MaxIpm, desiredIpm));
            double velCounts = ipm / 60.0 * s.Cpi;          // user units/min -> counts/sec
            _lastBridgeMotionMs = Environment.TickCount;
            _km.WriteLine("JogAtAccel" + s.JogCh + "=" + N(velCounts) + " " + N(s.JogAccelCounts));
            return true;
        }

        // Raw console step: distance + velocity + accel travel WITH the command
        // (MoveRelAtVelAccelN=D V A), all in COUNTS. Behavior-identical to the old
        // StartRelativeMoveTo path (same distance/vel/accel), but nothing is left on
        // the channel afterward -- that residue was the slow-rapids bug. Non-blocking
        // (WriteLine): the move runs async, so the poll loop is never stalled.
        public bool JogStepSel(int sel, double stepInches)
        {
            var s = _sel[sel];
            if (!CanJogCh(s.JogCh)) return false;
            double distCounts = stepInches * s.Cpi;
            double velCounts  = Tune.StepJogFeedIpm / 60.0 * s.Cpi;
            _lastBridgeMotionMs = Environment.TickCount;
            _km.WriteLine("MoveRelAtVelAccel" + s.JogCh + "=" + N(distCounts) + " "
                          + N(velCounts) + " " + N(s.StepAccelCounts));
            return true;
        }

        // Stop a running continuous jog. JogAtAccelN=0 A decelerates to a stop at
        // accel A (Dynomotion's documented stop form -- verified as the reliable jog
        // kill in bench tests 2026-07-19). Symmetric decel: reuse the jog's own accel.
        public void StopJogSel(int sel)
        {
            var s = _sel[sel];
            _lastBridgeMotionMs = Environment.TickCount;
            _km.WriteLine("JogAtAccel" + s.JogCh + "=0 " + N(s.JogAccelCounts));
        }

        // Belt-and-suspenders jog kill for the E-stop path. Issues Jog(0) on EVERY
        // KFLOP channel unconditionally, not just the one _jogChannel is tracking.
        // Necessary because _jogging can go false at the instant E-stop hits (e.g.
        // cleared by axis-select the tick before) while a Jog() is still running on
        // the physical channel — the runaway seen 2026-07-19 during finding #1 test.
        // try/catch per-channel so a disabled/unconfigured channel throwing does not
        // abort the loop — every remaining channel still gets its stop.
        public void StopAllChannels()
        {
            _lastBridgeMotionMs = Environment.TickCount;
            for (int ch = 0; ch < NumChan; ch++)
            {
                // JogAtAccelN=0 A -- decel-stop at the deliberately-large E-stop accel
                // (see EStopDecelCounts). try/catch per-channel so one bad channel
                // can't abort the sweep; every remaining channel still gets its stop.
                try { _km.WriteLine("JogAtAccel" + ch + "=0 " + N(EStopDecelCounts)); }
                catch { /* keep going */ }
            }
        }

        // Cancel any in-flight GOTOZ (F2) sequence. Called from the E-stop path so
        // an E-stop mid-Z-retract cannot let the sequencer fire the X/Y move once
        // Z reaches clearance (Tom's specific note in the 2026-07-16 review).
        public void CancelGotoz()
        {
            _gotozPhase = 0;
            _gotozState = ZeroState.Idle;
        }

        // ---- zero (targets the selection's COORDINATE axis) --------------------

        public void RequestZeroSel(int sel, double target = 0.0)
            => RequestZero(_sel[sel].DroSlot, target);

        void RequestZero(int coordAxis, double target)
        {
            if (coordAxis < 0 || coordAxis >= 6) { _zeroState = ZeroState.BadAxis; return; }
            _lastSentAxis = coordAxis;
            PostCmd(CMD_ZERO, coordAxis);          // reliable CMD channel (like spindle/S%)
            _zeroState   = ZeroState.Pending;      // -> Ok in PollZero so the LCD 'ZEROED' fires each time
            _zeroStartMs = Environment.TickCount;
        }

        // Short guard after a zero so rapid taps can't flood KMotionCNC. The zero
        // is fire-and-forget over the reliable CMD channel; there's nothing to poll.
        public bool ZeroBusy => (Environment.TickCount - _zeroStartMs) < ZeroGuardMs;

        public ZeroState PollZero()
        {
            // Optimistic confirm: the CMD channel + native Set are reliable, so a
            // moment after dispatch we call it done (drives the LCD 'ZEROED'). If a
            // Set ever visibly misses, the DRO shows it and you re-zero.
            if (_zeroState == ZeroState.Pending &&
                Environment.TickCount - _zeroStartMs >= ZeroConfirmMs)
                _zeroState = ZeroState.Ok;
            return _zeroState;
        }

        // ---- GOTOZ (F2): return-to-work-origin, sequenced by Z POSITION ----------
        // Phase 0 retracts Z to clearance; the X/Y move (phase 1) is only issued
        // once Z has physically REACHED clearance -- so a HOLD or E-stop mid-retract
        // (Z stalls below clearance) never fires the X/Y move. GotozBusy is true for
        // the whole sequence (debounces re-taps + gates the F2 button).

        public void RequestGotoz()
        {
            PostCmd(CMD_GOTOZ, 0.0);              // phase 0: retract Z
            _gotozPhase   = 1;
            _gotozState   = ZeroState.Pending;
            _gotozStartMs = Environment.TickCount;
        }

        public bool GotozBusy => _gotozPhase != 0;

        public ZeroState PollGotoz()
        {
            if (_gotozPhase == 0) return _gotozState;

            if (Environment.TickCount - _gotozStartMs > GotozMaxMs)
            {
                _gotozPhase = 0;                 // stalled (e.g. E-stop) -> abort; X/Y never fired
                _gotozState = ZeroState.Timeout;
                Console.WriteLine("Go-to-zero timed out and was aborted (X/Y never moved). "
                                + "Check for an E-stop or a stalled Z.");
                return _gotozState;
            }

            if (_gotozPhase == 1)
            {
                // Wait until Z has actually reached clearance, THEN move X & Y.
                double z = _km.GetUserDataDouble(UD_DRO + 2);
                if (z >= Tune.GotozZClearInch - GotozPosEps)
                {
                    PostCmd(CMD_GOTOZ, 1.0);     // phase 1: X & Y -> work zero
                    _gotozPhase   = 2;
                    _gotozStartMs = Environment.TickCount;
                }
            }
            else if (_gotozPhase == 2)
            {
                double x = _km.GetUserDataDouble(UD_DRO + 0);
                double y = _km.GetUserDataDouble(UD_DRO + 1);
                if (Math.Abs(x) < GotozPosEps && Math.Abs(y) < GotozPosEps)
                {
                    _gotozPhase = 0;
                    _gotozState = ZeroState.Ok;
                }
            }
            return _gotozState;
        }

        // ---- F3 Machine ON/OFF: read the live enable state; toggle via CMD channel.
        // Both the pendant LCD and the KMotionCNC screen indicator read this one
        // state so they always agree. Bridge only sends the toggle when idle.
        public bool MachineEnabled => (int)_km.GetUserDataDouble(UD_MACHINE) != 0;

        public void RequestMachineToggle()
        {
            PostCmd(CMD_MACHINE, 0.0);
        }

        // ---- screen "Machine Status" button -> lock/unlock toggle request ------
        // JPB_Machine_Toggle.c (run by the button) sets UD_SCREEN_TOGGLE = 1. The
        // bridge polls it, applies the SAME idle gate F3 uses, then relays through
        // RequestMachineToggle -- so it never writes UD_CMD_REQ directly (that cell
        // stays owned solely by PendantService.c). The bridge clears the flag.
        public bool ScreenTogglePending() => _km.GetUserDataDouble(UD_SCREEN_TOGGLE) != 0.0;
        public void ClearScreenToggle()   => _km.SetUserDataDouble(UD_SCREEN_TOGGLE, 0.0);

        // ---- interpreter command relays ---------------------------------------

        public void RequestExecute()           => PostCmd(CMD_EXECUTE, 0.0);
        public void RequestHalt()              => PostCmd(CMD_HALT, 0.0);
        public void RequestEStopFull()         => PostCmd(CMD_ESTOP, 0.0);
        public void RequestMCode(int mcode)    => PostCmd(CMD_MCODE, mcode);
        public void AdjustFeedOverride(double factor) => PostCmd(CMD_FRO_INC, factor);
        public void RequestSpindle(bool on)    => PostCmd(CMD_SPINDLE, on ? 1.0 : 0.0);
        public void RequestSpindleOverride(double frac) => PostCmd(CMD_SSO, frac);

        void PostCmd(int cmd, double arg)
        {
            _km.SetUserDataDouble(UD_CMD_ARG, arg);
            _km.SetUserDataDouble(UD_CMD_REQ, cmd);
        }

        // ---- hold / abort ------------------------------------------------------

        public void Hold()   => _km.Feedhold();
        public void Resume() => _km.ResumeFeedhold();
        public void Abort()  => _km.CoordMotion.Abort();

        // ---- per-interval upkeep ----------------------------------------------

        public void Service()
        {
            var st = _km.CurrentStatus;
            bool moving = false;
            for (int ch = 0; ch < NumChan; ch++)
            {
                double d = st.GetDestination(ch);
                if (Math.Abs(d - _lastDest[ch]) > DestEpsilonCounts) moving = true;
                _lastDest[ch] = d;
            }
            bool ours = (Environment.TickCount - _lastBridgeMotionMs) < MotionTailMs;
            if (moving && !ours) _idleSamples = 0;
            else if (_idleSamples < IdleSamplesRequired) _idleSamples++;

            double hb = _km.GetUserDataDouble(UD_HEARTBEAT);
            if (hb == _lastHeartbeat) { if (_heartbeatStalls < int.MaxValue) _heartbeatStalls++; }
            else { _heartbeatStalls = 0; _lastHeartbeat = hb; }
        }

        public void Dispose()
        {
            try { if (Connected) _km.KillProgramThreads(ServiceThread); } catch { }
            try { if (_km != null) _km.Dispose(); } catch { }
        }
    }
}
