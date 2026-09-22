/*
 * Bridge.cs  --  iMach P4-S -> KFLOP pendant bridge, Stage 2
 *
 * Ties the Stage-1 Pendant to KflopLink. Hot loop reads the pendant, maps inputs
 * to jog / zero / command actions, and refreshes the LCD.
 *
 * USER-TUNABLE speed/feel values live in Tune.cs. This file holds behavior.
 *
 * SELECTION: axes X A Y B Z C (Tune.Axes[] enable table). The three select
 * buttons each toggle a PAIR, skipping disabled members: [X|A] [Y|B] [Z|C]
 * (Z is the primary/first press on the vertical button). A/B are rotary (deg).
 * MODES (latched _modeCode): S step | Vv velocity | Cs continuous | C% Cs-rate |
 * F% feed-ovr | S% spindle-ovr (deferred).
 *   Vv = linear in measured wheel speed (leaky integrator), 0..cap.
 *   Cs = Tune.CsSetPoint of cap, dialed 0..200% by C%, with Tune.CsStepsToStart
 *        delay-start (ignore first N detents so a nudge won't lurch).
 *   Step = current step size, accumulated + committed per tick.
 * En: Vv/Cs half-speed | Step size-select (stopped) | C% dial | two-step confirm
 * (Zero/Start/Hold, En held both presses, inhibited while jogging). E-Stop
 * always immediate.
 *
 * BUTTONS (moving to the authentic P4-S flow, per the VistaCNC manual):
 *   ZERO (F1): HOLD the F1 button (Zplus, a level) -> LCD previews "ZERO x?";
 *   a clean EN tap executes. This matches how the pendant firmware reports the
 *   F-buttons (bit held while pressed) and avoids the fragile tap-twice edges.
 *   Start/Hold, Stop, and Spindle use the same HOLD-button + tap-EN flow.
 *   SPINDLE (Btn 0x02): HOLD -> LCD "SPINDLE" / "START?|STOP?"; EN tap toggles
 *   the spindle enable (KFLOP bit 156) directly via PendantService (manual 4.3).
 *
 * LINK-LOSS: Run() fails safe. It detects a lost KFLOP two ways, using no extra
 * board pokes: (a) STALE reads -> the heartbeat stops advancing -> ServiceAlive
 * goes false; (b) THROWING reads -> a consecutive upkeep-failure count trips.
 * On loss it stops touching the board immediately (marks the link lost so
 * teardown won't poke it, and does NOT stop-jog a dead board) and returns true
 * so Program.cs exits cleanly. Returns false on a normal Stop() (Ctrl+C).
 */

using System;
using System.Diagnostics;
using System.Threading;

namespace iMachKflop
{
    // Why Run() returned: a normal Ctrl+C stop, a KFLOP link loss, or a pendant
    // input (USB) loss. Program.cs branches on this -- LinkLost/PendantLost both
    // exit for Task Scheduler to relaunch, but only LinkLost can still paint the
    // LCD (on PendantLost the pendant is gone).
    public enum BridgeExit { Stopped, LinkLost, PendantLost }

    public sealed class Bridge
    {
        // ---- internal (non-user-facing) ----
        const double VelFractionEps   = 0.02;
        const int    ContIdleStopMs   = 120;
        const int    InitBannerMs     = 2000;   // how long the init-load banner (name/"Loaded") holds the LCD
        const double IpmSendEps       = 0.10;
        const double FroStepFactor    = 1.03;
        const int    UpkeepIntervalMs = 50;
        const int    UpkeepThrowLimit = 6;    // ~300ms of failing board ops -> link lost
        const bool   EnableCycleStart = true;
        const bool   EnableFroOverride= true;
        const int    AxisIndicatorShift = 4;

        const int ModeCodeStep  = 0, ModeCodeCont = 1, ModeCodeF = 2,
                  ModeCodeVel   = 3, ModeCodeCRate = 4, ModeCodeSovr = 5;

        // Indices into Tune.Buttons[] -- the action-button enable table. EN and
        // E-Stop are absent on purpose: they are never gateable.
        const int BtnF1 = 0, BtnF2 = 1, BtnF3 = 2,
                  BtnSpindle = 3, BtnStart = 4, BtnStop = 5;

        // A disabled action button is FULLY INERT: it never arms, never previews
        // on the LCD, and ignores the EN tap.
        static bool BtnOn(int b) => Tune.Buttons[b].Enabled;

        const int AxX = KflopLink.AxX, AxA = KflopLink.AxA,
                  AxY = KflopLink.AxY, AxB = KflopLink.AxB,
                  AxZ = KflopLink.AxZ, AxC = KflopLink.AxC;

        enum Arm { None, Zero, Gotoz, Machine, Start, Stop, Spindle }

        readonly Pendant   _pendant;
        readonly KflopLink _kflop;
        readonly Stopwatch _clock = Stopwatch.StartNew();
        readonly double[]  _dro   = new double[6];

        volatile bool _run;

        int  _sel     = AxX;   // set to the first ENABLED axis at Run() start
        int  _prevSel = AxX;
        int  _modeCode = ModeCodeStep;
        int  _csModeTrack = -1;

        bool   _jogging;
        int    _jogChannel = -1;   // axis a continuous jog was commanded on; independent of _sel
                                   // so a mid-jog axis-select still stops the RIGHT channel (T.K. 7/16)
        double _lastIpmSent;
        long   _lastMpgActivityMs;
        double _csRate = 1.0;
        double _ssoFrac = Tune.SsoDefault;   // S% spindle-speed override (1.0 = 100%), pendant-owned
        int    _pendingSteps;
        int    _stepIdx;

        bool   _csStarted;              // Cs delay-start latch
        int    _csStartAccum;           // detents seen before Cs starts

        double _wheelRate;
        long   _lastWheelMs;

        Arm  _armed = Arm.None;
        bool _zeroExecuted;             // ZERO: latch so one EN tap fires once per F1 hold
        bool _gotozExecuted;            // GOTOZ: latch so one EN tap fires once per F2 hold
        bool _machineExecuted;          // F3: latch so one EN tap toggles once per F3 hold
        bool _startExecuted;            // START/HOLD: one action per button hold
        bool _stopExecuted;             // STOP/REW: one action per button hold
        bool _spindleExecuted;          // SPINDLE: one toggle per button hold
        bool _spindleOn;                // cached live spindle enable (bit 156), refreshed each upkeep

        PendantInput _prev = new PendantInput();
        bool _held;

        long      _lastUpkeepMs;
        long      _lastInputMs;                  // last non-null pendant Read() -- pendant-input watchdog
        ZeroState _lastZero = ZeroState.Idle;
        long      _zeroMsgUntilMs;
        ZeroState _lastGotoz = ZeroState.Idle;
        long      _gotozMsgUntilMs;
        bool      _lastMachineEnabled = true;   // for detecting the on/off transition
        long      _machineOnMsgUntilMs;          // briefly show "Machine / ON" on re-enable
        long      _cfgMsgUntilMs;                // init-load banner window (name + "Loaded")
        int       _cfgInitId;                    // which init identity that banner names (1 Std / 2 Knee Z / 3 PCB)

        long NowMs => _clock.ElapsedMilliseconds;
        double StepInches => Tune.StepSizes[_stepIdx];

        public Bridge(Pendant pendant, KflopLink kflop)
        {
            _pendant = pendant;
            _kflop   = kflop;
            _lastWheelMs = NowMs;
        }

        public void Stop() => _run = false;

        // At least one JOG mode (S, Vv, or Cs) must be enabled in Tune, or the
        // pendant could not jog at all. Fail loudly at launch rather than leaving
        // the operator to discover it at the machine.
        static bool AnyJogModeEnabled()
            => Tune.Modes[ModeCodeStep].Enabled
            || Tune.Modes[ModeCodeVel].Enabled
            || Tune.Modes[ModeCodeCont].Enabled;

        // First enabled mode, preferring a jog mode -- used to land on a valid
        // mode at startup instead of assuming S is on.
        static int FirstEnabledMode()
        {
            int[] order = { ModeCodeStep, ModeCodeVel, ModeCodeCont,
                            ModeCodeCRate, ModeCodeF, ModeCodeSovr };
            foreach (int m in order)
                if (Tune.Modes[m].Enabled) return m;
            return ModeCodeStep;   // unreachable: AnyJogModeEnabled() gates Run()
        }

        // Returns true if it exited because the KFLOP link was lost (Program.cs
        // should exit); false on a normal Stop() (Ctrl+C).
        public BridgeExit Run()
        {
            if (!AnyJogModeEnabled())
            {
                Console.WriteLine("[TUNE] No jog mode is enabled (S, Vv, Cs are all off in Tune.Modes).");
                Console.WriteLine("       The pendant could not jog the machine. Enable at least one and rebuild.");
                return BridgeExit.Stopped;
            }

            _run = true;
            _sel = _prevSel = _kflop.FirstEnabledAxis();   // start on a valid, enabled axis
            _modeCode = _csModeTrack = FirstEnabledMode(); // start in a valid, enabled mode
            _lastInputMs = NowMs;                          // arm the watchdog from a clean baseline
            bool linkLost    = false;
            bool pendantLost = false;
            int  upkeepThrows = 0;

            while (_run)
            {
                try
                {
                    PendantInput inp = _pendant.Read(5);
                    if (inp != null)
                    {
                        _lastInputMs = NowMs;              // fresh input -> watchdog stays disarmed
                        int mpg = _pendant.MpgDelta(inp.Mpg);
                        UpdateWheelRate(mpg);
                        UpdateMode(inp);
                        UpdateAxis(inp);
                        HandleJog(inp, mpg);
                        HandleButtons(inp);
                        _prev = inp;
                    }
                }
                catch
                {
                    Thread.Sleep(5);
                }

                try
                {
                    if (MaybeUpkeep()) upkeepThrows = 0;   // a full upkeep cycle succeeded
                }
                catch
                {
                    upkeepThrows++;                        // upkeep attempted and threw
                }

                // Screen "Machine Status" click -> same lock/unlock toggle as F3.
                // Clear the flag on sight (so it fires once per click), then relay
                // only when idle, through the identical path F3 takes. Wrapped so a
                // board hiccup here can't take down the loop.
                try
                {
                    if (_kflop.ScreenTogglePending())
                    {
                        _kflop.ClearScreenToggle();
                        if (_kflop.MachineIdle) _kflop.RequestMachineToggle();
                    }
                }
                catch { }

                // Pendant-input watchdog (USB-drop RUNAWAY guard). Runs here, on the
                // null path too, unlike ContIdleStopMs which lives in HandleJog and
                // only runs when a report arrives. The KFLOP is a separate USB device,
                // so a pendant loss still lets us command the stop.
                long inputStall = NowMs - _lastInputMs;
                if (_jogging && inputStall > Tune.JogWatchdogMs)
                {
                    // Safety stop: the pendant went quiet mid-jog. Decel-stop the
                    // jogging channel at its own accel (same as a normal wheel-stop).
                    try { StopJog(); } catch { }
                }
                if (inputStall > Tune.PendantDeadMs)
                {
                    pendantLost = true;                    // sustained -> pendant gone; exit to relaunch
                    break;
                }

                // Link-loss detection -- no extra board pokes (both are local reads):
                //   throwing board ops (count) OR stalled heartbeat (ServiceAlive).
                if (upkeepThrows >= UpkeepThrowLimit || !_kflop.ServiceAlive)
                {
                    linkLost = true;
                    _kflop.MarkLinkLost();                 // teardown must not poke a dead board
                    break;
                }
            }

            // Only stop the jog if the board is still alive. On a pendant loss the
            // KFLOP is fine and the watchdog already stopped the jog; on a KFLOP
            // link loss the board is dead, so don't poke it.
            if (!linkLost) { try { if (_jogging) StopJog(); } catch { } }
            return linkLost    ? BridgeExit.LinkLost
                 : pendantLost ? BridgeExit.PendantLost
                 : BridgeExit.Stopped;
        }

        void UpdateWheelRate(int mpg)
        {
            long now = NowMs;
            double dt = (now - _lastWheelMs) / 1000.0;
            _lastWheelMs = now;
            if (dt < 0) dt = 0;
            _wheelRate *= Math.Exp(-dt / Tune.WheelTauSec);
            if (mpg != 0)
            {
                // REVERSAL FIX (2026-07-25, pre-existing Vv bug found at the machine):
                // if the wheel is now turning OPPOSITE to the momentum still held in
                // the leaky integrator, dump that momentum so the rate tracks the new
                // direction from its very first detent. Without this, a slower/weaker
                // reverse spin may never overcome the accumulated old-direction rate,
                // so the axis keeps moving the old way until the wheel stops entirely.
                // The axis is then commanded down through zero (stop) and back up the
                // other way. Only Vv uses _wheelRate; Cs already reverses on the
                // instantaneous detent sign, so it is unaffected.
                if (Math.Sign(mpg) != Math.Sign(_wheelRate))
                    _wheelRate = 0.0;
                _wheelRate += mpg / Tune.WheelTauSec;
            }
        }

        // ---- mode tracking -----------------------------------------------------

        void UpdateMode(PendantInput inp)
        {
            if      (Rising(inp.ModeStep, _prev.ModeStep))
                _modeCode = TogglePair(_modeCode, ModeCodeStep, ModeCodeVel);
            else if (Rising(inp.ModeCont, _prev.ModeCont))
                _modeCode = TogglePair(_modeCode, ModeCodeCont, ModeCodeCRate);
            else if (Rising(inp.ModeF, _prev.ModeF))
                _modeCode = TogglePair(_modeCode, ModeCodeF,    ModeCodeSovr);

            if (_jogging && !ModeMovesAxis(_modeCode)) StopJog();

            if (_modeCode != _csModeTrack) { ResetCsStart(); _csModeTrack = _modeCode; }
        }

        static bool ModeMovesAxis(int m)
            => m == ModeCodeStep || m == ModeCodeVel || m == ModeCodeCont;

        // Toggle a mode button's PAIR, honouring the Tune enable table:
        //   - both halves enabled  -> normal flip between them
        //   - one half disabled    -> the button always lands on the enabled half
        //   - both halves disabled -> the button does nothing (mode unchanged)
        static int TogglePair(int current, int a, int b)
        {
            bool aEn = Tune.Modes[a].Enabled;
            bool bEn = Tune.Modes[b].Enabled;

            if (!aEn && !bEn) return current;      // whole pair off -> inert button
            if (!bEn) return a;                    // only 'a' available
            if (!aEn) return b;                    // only 'b' available
            return (current == a) ? b : a;         // both on -> authentic flip
        }

        void ResetCsStart() { _csStarted = false; _csStartAccum = 0; }

        // ---- axis selection ----------------------------------------------------

        void UpdateAxis(PendantInput inp)
        {
            if      (Rising(inp.AxisXA, _prev.AxisXA)) SelectPair(AxX, AxA);
            else if (Rising(inp.AxisYB, _prev.AxisYB)) SelectPair(AxY, AxB);
            else if (Rising(inp.AxisZC, _prev.AxisZC)) SelectPair(AxZ, AxC);  // Z = first press

            if (_sel != _prevSel)
            {
                if (_jogging) StopJog();
                _pendingSteps = 0;
                ResetCsStart();
                _prevSel = _sel;
            }
        }

        // Toggle within a select button's PAIR, skipping disabled members.
        //   both members enabled  -> toggle primary<->secondary
        //   one member enabled    -> the button always lands on that one
        //   neither enabled       -> button is inert (selection unchanged)
        // "primary" is the first press when arriving from another pair.
        void SelectPair(int primary, int secondary)
        {
            bool pEn = _kflop.SelEnabled(primary);
            bool sEn = _kflop.SelEnabled(secondary);
            if (!pEn && !sEn) return;                              // whole pair off
            if      (_sel == primary   && sEn) _sel = secondary;   // toggle out
            else if (_sel == secondary && pEn) _sel = primary;     // toggle back
            else                               _sel = pEn ? primary : secondary;  // enter pair
        }

        // ---- jog / override -- gated on the LATCHED _modeCode -------------------

        void HandleJog(PendantInput inp, int mpg)
        {
            // E-STOP LEVEL-BASED INHIBIT (Tom's pattern for the KFLOP init E-stop
            // applies here too — level actions every pass while held, edge only for
            // the DoPC/state-reset which lives in HandleButtons). Fixes the runaway
            // observed 2026-07-19: with the wheel still spinning and Cs mode armed,
            // a pure rising-edge handler let HandleJog re-issue a Jog on the very
            // next tick, so motion paused for the decel then resumed at full rate.
            //
            // While inp.EStop is true: (1) hammer Jog(0) on every channel — cheap,
            // idempotent, kills any Jog that slipped through; (2) refuse all jog
            // input this tick; (3) reset Cs delay-start so accumulated detents
            // during the hold don't ratchet the axis up the instant E-stop releases.
            if (inp.EStop)
            {
                _kflop.StopAllChannels();
                _jogging     = false;
                _jogChannel  = -1;
                _lastIpmSent = 0;
                _pendingSteps = 0;
                ResetCsStart();
                return;
            }

            // While the ZERO (F1) button is held we're doing a zero, not jogging.
            // Suppress all jog so a stray wheel bump can't micro-jog, flicker
            // _jogging, flash the LCD, and steal the EN execute edge in
            // HandleButtons (which made the zero take one or two tries).
            if (inp.Zplus)
            {
                if (_jogging) StopJog();
                return;
            }

            switch (_modeCode)
            {
                case ModeCodeStep:
                    if (_jogging) StopJog();
                    if (inp.En)
                    {
                        if (_kflop.MachineIdle && mpg != 0)
                            _stepIdx = ((_stepIdx + Math.Sign(mpg)) % Tune.StepSizes.Length + Tune.StepSizes.Length) % Tune.StepSizes.Length;
                    }
                    else if (mpg != 0) _pendingSteps += mpg;
                    return;

                case ModeCodeVel:
                {
                    double frac = Math.Min(Math.Abs(_wheelRate) / Tune.FullScaleDPS, 1.0);
                    if (frac > VelFractionEps)
                    {
                        _lastMpgActivityMs = NowMs;
                        double mul = inp.En ? Tune.HalfSpeedMul : 1.0;
                        SetJogIpm(Math.Sign(_wheelRate) * frac * _kflop.SelMaxIpm(_sel) * mul);
                    }
                    else if (_jogging && NowMs - _lastMpgActivityMs > ContIdleStopMs)
                        StopJog();
                    return;
                }

                case ModeCodeCont:
                    if (mpg != 0)
                    {
                        _lastMpgActivityMs = NowMs;
                        if (!_csStarted)
                        {
                            _csStartAccum += Math.Abs(mpg);
                            if (_csStartAccum >= Tune.CsStepsToStart) _csStarted = true;
                        }
                        if (_csStarted)
                        {
                            double mul = inp.En ? Tune.HalfSpeedMul : 1.0;
                            double ipm = Tune.CsSetPoint * _kflop.SelMaxIpm(_sel) * _csRate * mul;
                            SetJogIpm(Math.Sign(mpg) * ipm);
                        }
                    }
                    else if (NowMs - _lastMpgActivityMs > ContIdleStopMs)
                    {
                        if (_jogging) StopJog();
                        ResetCsStart();
                    }
                    return;

                case ModeCodeCRate: // C% dial: En-held + wheel -> 0..200%, additive
                    if (_jogging) StopJog();
                    if (inp.En && mpg != 0)
                    {
                        _csRate += mpg * Tune.CsPctPerDetent;
                        _csRate = Math.Max(Tune.CsRateFloor, Math.Min(Tune.CsRateCeil, _csRate));
                    }
                    return;

                case ModeCodeF:
                    if (_jogging) StopJog();
                    if (EnableFroOverride && mpg != 0)
                    {
                        int d = Math.Max(-10, Math.Min(10, mpg));
                        _kflop.AdjustFeedOverride(Math.Pow(FroStepFactor, d));
                    }
                    return;

                case ModeCodeSovr:
                    if (_jogging) StopJog();
                    if (mpg != 0)
                    {
                        // S% spindle-speed override: additive per detent, faithful to
                        // vc-p4s.hal (halui.spindle-override.scale 0.001 = 0.1%/detent),
                        // clamped to Tune.SsoMin..SsoMax. The pendant owns the value and
                        // pushes the absolute SSO to KMotionCNC.
                        _ssoFrac += mpg * Tune.SsoStepPerDetent;
                        _ssoFrac = Math.Max(Tune.SsoMin, Math.Min(Tune.SsoMax, _ssoFrac));
                        _kflop.RequestSpindleOverride(_ssoFrac);
                    }
                    return;
            }
        }

        void SetJogIpm(double ipm)
        {
            // Keep a sustained jog classified as "ours" even when the command below
            // is deduped -- otherwise MachineIdle flips false at steady speed and
            // CanJogCh blocks the next velocity change, including a reversal (root
            // cause of the Vv reversal bug -- see KflopLink.MarkJogActive).
            _kflop.MarkJogActive();
            if (_jogging && Math.Abs(ipm - _lastIpmSent) < IpmSendEps) return;
            if (_kflop.JogContinuousIpm(_sel, ipm))
            {
                _jogging     = true;
                _jogChannel  = _sel;          // remember the axis that's actually moving
                _lastIpmSent = ipm;
            }
        }

        void StopJog()
        {
            if (_jogChannel >= 0) _kflop.StopJogSel(_jogChannel);   // stop the axis that was jogging,
            _jogging     = false;                                   // NOT whatever _sel is right now
            _jogChannel  = -1;
            _lastIpmSent = 0;
        }

        // ---- buttons: E-stop + ZERO (authentic hold+EN) + Start/Hold (old) -----

        void HandleButtons(PendantInput inp)
        {
            // E-Stop: always immediate. HARDWIRED E-STOP IS STILL PRIMARY — this is
            // the software backstop, layered on top per Tom Kerekes 2026-07-11.
            //
            // Belt-and-suspenders (Tom's finding #3, 2026-07-16 — RequestEStopFull /
            // StopAllChannels / CancelGotoz all existed or were added but nothing was
            // calling them from here; only CoordMotion.Abort was, which does NOT
            // affect direct-axis Jogs — verified at the machine 2026-07-19):
            //   1. RequestHalt -> CMD_HALT -> DoPC(PC_COMM_HALT) via the service:
            //      halts the KMotionCNC INTERPRETER. NEEDED — CoordMotion.Abort()
            //      alone cancels only the coord move in flight; the interpreter
            //      just plans the next G-code line and hands it back. Same DoPC
            //      path as the pendant STOP button — known safe.
            //   2. RequestEStopFull -> CMD_ESTOP -> DoPC_KeepAlive(PC_COMM_ESTOP):
            //      DEFERRED AGAIN 2026-07-19. First attempted with raw DoPC; that
            //      wedged thread 7 (Tom's warning about "stock DoPC has no timeout").
            //      Fixed the thread 7 side with DoPC_KeepAlive in PendantService.c.
            //      But testing revealed the KMotionCNC side ALSO locks up when
            //      PC_COMM_ESTOP arrives during a running G-code job — even with
            //      the KeepAlive protecting thread 7. Bridge loses the link,
            //      KMotionCNC UI freezes, close + init reload required to recover.
            //      Likely cause: KMotionCNC's E-stop handler does heavy motion-queue
            //      teardown that blocks KMotionServer for other clients. Tom's
            //      advice ("wire the button to the CMD_ESTOP path") cannot be
            //      applied as-is; another approach is needed. Kept commented so
            //      the intent + call site are preserved.
            //   3. Abort() -> CoordMotion.Abort(): cancels the coord move in flight.
            //   4. StopAllChannels(): Jog(0) on every KFLOP channel, unconditional —
            //      NOT gated on _jogging, which can be false at the instant E-stop
            //      hits (e.g. cleared by axis-select the prior tick).
            //   5. CancelGotoz(): reset the F2 sequencer so an E-stop mid-Z-retract
            //      cannot let it fire the X/Y move once Z reaches clearance.
            if (Rising(inp.EStop, _prev.EStop))
            {
                _kflop.RequestHalt();          // halt the interpreter (safe DoPC(PC_COMM_HALT))
                // _kflop.RequestEStopFull();   // DEFERRED — see note (2) above
                _kflop.Abort();
                _kflop.StopAllChannels();
                _kflop.CancelGotoz();

                _jogging = false;
                _jogChannel = -1;
                _lastIpmSent = 0;
                _pendingSteps = 0;
                _held = false;
                _armed = Arm.None;
                _zeroExecuted = false;
                _gotozExecuted = false;
                _machineExecuted = false;
                _startExecuted = false;
                _stopExecuted = false;
                _spindleExecuted = false;
                return;
            }

            // --- F1 / ZERO: authentic P4-S flow -- HOLD F1 (Zplus, a level),
            //     tap EN to execute. Bit stays set while held; EN is a clean tap. ---
            if (inp.Zplus && !_jogging && BtnOn(BtnF1))
            {
                if (!_zeroExecuted)
                {
                    _armed = Arm.Zero;                          // preview "ZERO x?" while held
                    if (Rising(inp.En, _prev.En))               // EN tap = execute (once per hold)
                    {
                        // SERIALIZE: only fire when the machine is idle AND the previous
                        // zero has fully completed (ZERO_REQ confirmed clear). Firing while
                        // a prior zero is still in flight lets the handler's ack (ZERO_REQ=0)
                        // clobber the new request on the shared ZERO_REQ slot -- that race
                        // is the intermittent-zero bug (failed axis stays stuck, next fails).
                        // If gated, the latch stays open so a re-tap of EN retries cleanly.
                        if (_kflop.MachineIdle && !_kflop.ZeroBusy)
                        {
                            _kflop.RequestZeroSel(_sel, 0.0);
                            _zeroExecuted = true;
                            _armed = Arm.None;                   // clear so the ZEROED result can show
                        }
                    }
                }
                return;
            }
            if (_armed == Arm.Zero) _armed = Arm.None;          // released F1 without executing
            _zeroExecuted = false;                              // reset once-per-hold latch

            // --- F2 / GOTOZ: HOLD F2 (XYplus, a level), tap EN to execute the
            //     return-to-work-origin move. v1 retracts Z only (never descends);
            //     v2 adds X&Y -> work zero sequenced behind it. Same flow as F1. ---
            if (inp.XYplus && !_jogging && BtnOn(BtnF2))
            {
                if (!_gotozExecuted)
                {
                    _armed = Arm.Gotoz;                         // preview "GOTOZ?" while held
                    if (Rising(inp.En, _prev.En))               // EN tap = execute (once per hold)
                    {
                        // Only fire when idle and not already running one (debounce).
                        // If gated, the latch stays open so a re-tap of EN retries.
                        if (_kflop.MachineIdle && !_kflop.GotozBusy)
                        {
                            _kflop.RequestGotoz();
                            _gotozExecuted = true;
                            _armed = Arm.None;                   // clear so the GOTOZ result can show
                        }
                    }
                }
                return;
            }
            if (_armed == Arm.Gotoz) _armed = Arm.None;         // released F2 without executing
            _gotozExecuted = false;                             // reset once-per-hold latch

            // --- F3 / Machine ON/OFF: HOLD F3 (XYminus), tap EN to toggle the axes'
            //     enable. Only fires when idle -- Machine OFF while moving isn't
            //     allowed (use HOLD/Resume for that). Knee is non-backdriveable, so
            //     disabling is safe. ---
            if (inp.XYminus && !_jogging && BtnOn(BtnF3))
            {
                if (!_machineExecuted)
                {
                    _armed = Arm.Machine;                       // preview "MACHINE?" while held
                    if (Rising(inp.En, _prev.En))               // EN tap = toggle (once per hold)
                    {
                        if (_kflop.MachineIdle)
                        {
                            _kflop.RequestMachineToggle();
                            _machineExecuted = true;
                            _armed = Arm.None;
                        }
                    }
                }
                return;
            }
            if (_armed == Arm.Machine) _armed = Arm.None;       // released F3 without executing
            _machineExecuted = false;                           // reset once-per-hold latch

            // --- START / HOLD (Btn 0x04): authentic P4-S flow (manual 4.1) --
            //     HOLD the button (a level), tap EN to execute. State toggle:
            //     held -> RESUME; machine idle -> cycle START; running -> feed HOLD.
            //     _startExecuted latches one action per hold (like ZERO). ---
            if (inp.Start && !_jogging && BtnOn(BtnStart))
            {
                if (!_startExecuted)
                {
                    _armed = Arm.Start;                          // preview START/HOLD/RESUME while held
                    if (Rising(inp.En, _prev.En))               // EN tap = execute (once per hold)
                    {
                        if (_held)                   { _kflop.Resume(); _held = false; }
                        else if (_kflop.MachineIdle) { if (EnableCycleStart) _kflop.RequestExecute(); }
                        else                         { _kflop.Hold();   _held = true;  }
                        _startExecuted = true;
                        _armed = Arm.None;
                    }
                }
                return;
            }
            if (_armed == Arm.Start) _armed = Arm.None;         // released without executing
            _startExecuted = false;

            // --- STOP / REW (Btn 0x08): authentic P4-S flow (manual 4.2) --
            //     HOLD the button, tap EN to execute STOP (halt the interpreter).
            //     REW/rewind has no distinct command in the relay, so STOP is the
            //     executed function; halting also clears any feed-hold state. ---
            if (inp.Stop && !_jogging && BtnOn(BtnStop))
            {
                if (!_stopExecuted)
                {
                    _armed = Arm.Stop;                          // preview "STOP?" while held
                    if (Rising(inp.En, _prev.En))
                    {
                        _kflop.RequestHalt();
                        _held = false;
                        _stopExecuted = true;
                        _armed = Arm.None;
                    }
                }
                return;
            }
            if (_armed == Arm.Stop) _armed = Arm.None;
            _stopExecuted = false;

            // --- SPINDLE ON/OFF (Btn 0x02): authentic P4-S flow (manual 4.3) --
            //     HOLD the SPINDLE button (a level), tap EN to toggle. The action
            //     is a DIRECT KFLOP bit-156 toggle (PendantService, no interpreter);
            //     we command the OPPOSITE of the live enable state (_spindleOn).
            //     Both VFDs are hardware-disabled on E-Stop, so this bit only ever
            //     gates an already-running spindle. ---
            if (inp.Spindle && !_jogging && BtnOn(BtnSpindle))
            {
                if (!_spindleExecuted)
                {
                    _armed = Arm.Spindle;                       // preview SPINDLE START?/STOP? while held
                    if (Rising(inp.En, _prev.En))               // EN tap = execute (once per hold)
                    {
                        _kflop.RequestSpindle(!_spindleOn);      // toggle: command the opposite state
                        _spindleExecuted = true;
                        _armed = Arm.None;
                    }
                }
                return;
            }
            if (_armed == Arm.Spindle) _armed = Arm.None;
            _spindleExecuted = false;
        }

        static bool Rising(bool now, bool was) => now && !was;

        // ---- throttled upkeep + LCD --------------------------------------------
        // Returns true if it ran a full upkeep cycle (board ops), false if it
        // early-returned within the interval. Throws propagate to Run().

        bool MaybeUpkeep()
        {
            long now = NowMs;
            if (now - _lastUpkeepMs < UpkeepIntervalMs) return false;
            _lastUpkeepMs = now;

            _kflop.Service();

            // Mid-session init swap: keyed on the init IDENTITY (var 56), which is
            // distinct per init, so it fires on EVERY init load -- including Knee Z <->
            // PCB, which share config id 2. When a different identity is published AND
            // it's safe (machine idle, not jogging), re-resolve knee/quill live (from
            // var 54) and raise the LCD banner -- no bridge restart. The inits republish
            // 56 in a forever loop, so a change noticed while busy simply applies on the
            // first safe upkeep -- deferred until safe, never dropped, never under a jog.
            if (_kflop.MachineIdle && !_jogging)
            {
                int liveInit = _kflop.ReadInitId();
                if (liveInit != _kflop.InitId && _kflop.ApplyInit(liveInit, _kflop.ReadConfigId()))
                {
                    _cfgInitId     = liveInit;
                    _cfgMsgUntilMs = now + InitBannerMs;
                    Console.WriteLine("Init loaded: " + InitBannerName(liveInit)
                        + " (config id " + _kflop.ConfigId + ").");
                }
            }

            if (_pendingSteps != 0)
            {
                if (_modeCode == ModeCodeStep)
                {
                    int n = _pendingSteps;
                    _pendingSteps = 0;
                    _kflop.JogStepSel(_sel, n * StepInches);
                }
                else _pendingSteps = 0;
            }

            _kflop.SnapshotWorkDros(_dro);
            _spindleOn = _kflop.SpindleOn();     // cache live spindle enable for the LCD + toggle

            ZeroState z = _kflop.PollZero();
            if (z != _lastZero)
            {
                _lastZero = z;
                if (z == ZeroState.Ok || z == ZeroState.BadAxis || z == ZeroState.Timeout)
                    _zeroMsgUntilMs = now + 1000;
            }

            ZeroState g = _kflop.PollGotoz();
            if (g != _lastGotoz)
            {
                _lastGotoz = g;
                if (g == ZeroState.Ok || g == ZeroState.BadAxis || g == ZeroState.Timeout)
                    _gotozMsgUntilMs = now + 1000;
            }

            bool machEn = _kflop.MachineEnabled;
            if (machEn != _lastMachineEnabled)
            {
                _lastMachineEnabled = machEn;
                if (machEn) _machineOnMsgUntilMs = now + 1000;   // briefly confirm "Machine / ON"
            }

            WriteLcd();
            return true;
        }

        byte BuildIndicator()
        {
            return (byte)((_kflop.DroSlot(_sel) << AxisIndicatorShift) | (_modeCode & 0x0F));
        }

        string ModeText()
        {
            switch (_modeCode)
            {
                case ModeCodeVel:   return "VEL  ";
                case ModeCodeCont:  return "CONT ";
                case ModeCodeCRate: return "CRT% ";
                case ModeCodeF:     return "FOVR ";
                case ModeCodeSovr:  return "SOVR ";
                default:            return "STEP ";
            }
        }

        string ArmPrompt()
        {
            switch (_armed)
            {
                case Arm.Zero:    return Tune.ZeroPrefix + _kflop.SelLabel(_sel) + "?";
                case Arm.Gotoz:   return Tune.GotozPrompt;
                case Arm.Machine: return _kflop.MachineEnabled ? Tune.MachPromptLock : Tune.MachPromptUnlk;
                case Arm.Start:   return _held ? Tune.ResumePrompt : (_kflop.MachineIdle ? Tune.StartPrompt : Tune.HoldPrompt);
                case Arm.Stop:    return Tune.StopPrompt;
                case Arm.Spindle: return _spindleOn ? Tune.SpindleOffP : Tune.SpindleOnP;
                default:          return "";
            }
        }

        string SsoText()
        {
            // Faithful to the pendant's integer % readout (manual 2.6 "OR 110%").
            // The value moves 0.1%/detent under the hood (vc-p4s.hal scale 0.001),
            // so the integer display ticks about every 10 detents -- exactly like
            // the original pendant.
            return "OR " + (int)Math.Round(_ssoFrac * 100.0) + "%";
        }

        // Rotary axes get their own (coarser) decimal count -- see Tune.RotaryDroDecimals
        // for why degrees can't use the linear setting. Linear axes are unchanged.
        static string DroFormat(bool rotary)
        {
            int dec = rotary ? Tune.RotaryDroDecimals
                             : Math.Max(2, Math.Min(4, Tune.DroDecimals));
            return "0." + new string('0', dec);
        }

        // Init name for the load banner, by identity (var 56).
        static string InitBannerName(int initId)
        {
            switch (initId)
            {
                case 1:  return Tune.InitNameStd;
                case 2:  return Tune.InitNameKnee;
                case 3:  return Tune.InitNamePcb;
                default: return Tune.InitNameUnknown;
            }
        }

        // Center a string in the 8-char LCD line (odd padding biases one space right).
        // A string already >= 8 chars is passed through (PutLine truncates it).
        const int LcdWidth = 8;
        static string Center(string s)
        {
            s = s ?? "";
            if (s.Length >= LcdWidth) return s;
            int pad  = LcdWidth - s.Length;
            int left = pad / 2;
            return new string(' ', left) + s + new string(' ', pad - left);
        }

        void WriteLcd()
        {
            // Init-load banner takes over the WHOLE screen briefly: name on line 1,
            // "Loaded" on line 2 (clears the DRO), then normal drawing resumes.
            if (NowMs < _cfgMsgUntilMs)
            {
                _pendant.WriteLcd(Center(InitBannerName(_cfgInitId)), Center(Tune.InitLoadedL2), BuildIndicator());
                return;
            }

            char label = _kflop.SelLabel(_sel);
            double val = _dro[_kflop.DroSlot(_sel)];
            string s  = val.ToString(DroFormat(_kflop.SelRotary(_sel)));
            string l1 = label + s;
            if (l1.Length > 8)
                l1 = label + (val >= 0 ? " +OVR" : " -OVR");

            string l2;
            bool showingSso = false;
            bool showingMachine = false;
            if (!_kflop.Connected || !_kflop.ServiceAlive) l2 = Tune.StatusNoLink;
            else if (_armed != Arm.None)                   l2 = ArmPrompt();
            else if (!_kflop.MachineEnabled)               { l2 = Tune.MachLockOn;  showingMachine = true; }
            else if (NowMs < _machineOnMsgUntilMs)         { l2 = Tune.MachLockOff; showingMachine = true; }
            else if (NowMs < _zeroMsgUntilMs)              l2 = _lastZero == ZeroState.Ok ? Tune.StatusZeroed : Tune.StatusZeroErr;
            else if (NowMs < _gotozMsgUntilMs)             l2 = Tune.StatusGotoz;
            else if (_modeCode == ModeCodeSovr)            { l2 = SsoText(); showingSso = true; }
            else if (_held)                                l2 = Tune.StatusHold;
            else if (!_kflop.MachineIdle)                  l2 = Tune.StatusBusy;
            else if (_modeCode == ModeCodeStep)            l2 = "STP" + StepInches.ToString("0.0000").Substring(1);
            else if (_modeCode == ModeCodeCRate)           l2 = "C%" + ((int)Math.Round(_csRate * 100));
            else
            {
                int fx = _kflop.ActiveFixture();
                string g = (fx >= 1 && fx <= 9) ? "G" + (53 + fx) : "G--";
                l2 = ModeText() + g;
            }

            // Spindle prompt takes over line 1 too, to match the manual's
            // two-line "SPINDLE" / "START?|STOP?" display (4.3). S% mode shows
            // the manual's "Spindle" / "OR nnn%" override readout (2.6).
            if (showingSso)                 l1 = Tune.SpindleOvLbl;
            else if (_armed == Arm.Spindle) l1 = Tune.SpindleLbl;
            else if (_armed == Arm.Machine || showingMachine) l1 = Tune.MachLbl;

            _pendant.WriteLcd(l1, l2, BuildIndicator());
        }
    }
}
