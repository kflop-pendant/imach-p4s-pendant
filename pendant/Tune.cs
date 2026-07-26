/*
 * ============================================================================
 *  Tune.cs  --  USER ADJUSTMENT SECTION  (the pendant's "config screen")
 * ============================================================================
 *  Two kinds of knob live here now:
 *
 *   1. SPEED / FEEL values (IPM caps, accels, step sizes, Vv/Cs tuning, GOTOZ
 *      feeds, S% range, DRO decimals) are loaded at STARTUP from an external
 *      text file -- pendant\pendant.conf -- by TuneConfig.Load(). Edit that file
 *      and RESTART the bridge; NO rebuild. The fields below are declared `static`
 *      and carry the compiled default only as documentation of the shipped value;
 *      the loader overwrites them, and the bridge REFUSES TO START if the file is
 *      missing/malformed (it never silently runs on these defaults).
 *
 *   2. Everything else -- hardware facts (counts-per-inch), the axis/mode/button
 *      enable tables, LCD strings, gate timings, AutoStart -- stays COMPILED here.
 *      Change one of these the old way: edit, rebuild, redeploy.
 *
 *  DESIGN NOTE -- "list only what you want":
 *  For anything that cycles on the pendant (step sizes, and later FRO / spindle
 *  presets), you write a LIST of exactly the values you want to cycle through.
 *  The pendant cycles precisely that list -- no blank slots to scroll past. Want
 *  3 step sizes? List 3. Want 5? List 5 (up to 9). First entry = the default.
 *
 *  Sections:
 *    1. MACHINE CONFIG    2. JOG SPEED CAPS    3. VELOCITY (Vv)
 *    4. CONTINUOUS (Cs)/C%   5. STEP    6. JOG RAMP    7. HALF-SPEED
 *    8. DISPLAY    9. DEFERRED (not wired yet)    10. DIAGNOSTICS
 * ============================================================================
 */

namespace iMachKflop
{
    static class Tune
    {
        // ===== 1. MACHINE CONFIG (hardware facts) ==============================

        // Knee-vs-quill on ch2 is chosen at RUNTIME from the config id your init
        // publishes (UserData double-index 54: 1 = Standard / quill on ch2,
        // 2 = PCB or Knee-Z / knee on ch2). So ONE build is correct for whichever
        // init you load. This constant is only a FALLBACK, used if that id can't
        // be read (it normally never is).
        public const bool KneeOnCh2 = true;

        // Counts per inch -- MEASURED machine facts. Change only if you retune.
        public const double CpiXY    = 233500.0;
        public const double CpiKnee  = 427000.0;
        public const double CpiQuill = 180000.0;

        // ----- ROTARY axes (A / B) -------------------------------------------
        // Counts per DEGREE (the rotary analogue of counts-per-inch). For a
        // direct-drive stepper: (counts per motor rev) / 360. A = 20000 cnt/rev,
        // direct drive -> 20000/360 = 55.5556 cnt/deg. B mirrors A (placeholder).
        public const double CpdA = 20000.0 / 360.0;   // 55.5556 counts/degree
        public const double CpdB = 20000.0 / 360.0;
        // Jog speed cap in DEG/MIN (the rotary analogue of the IPM caps above).
        // 1800 deg/min = 30 deg/sec -- a deliberately gentle first value while the
        // rotary is unproven; it's ~24x under the axis's G-code ceiling, so there's
        // huge margin and no step-loss worry (A is open-loop). Raise when happy.
        // [pendant.conf: DpmA, DpmB]
        public static double DpmA = 1800.0;
        public static double DpmB = 1800.0;
        // Accel for the rotary axes (counts/sec^2). Kept well under the init's
        // ch4 Accel=400000 so jog ramps stay gentle.
        // [pendant.conf: JogAccA, JogAccB, StepAccA, StepAccB]
        public static double JogAccA  = 100000.0;
        public static double JogAccB  = 100000.0;
        public static double StepAccA =  40000.0;
        public static double StepAccB =  40000.0;

        // ===== 1a. AXIS ENABLE TABLE ==========================================
        // ONE ROW PER AXIS. Set Enabled=false to hide an axis from the pendant
        // ENTIRELY -- it drops out of the select cycle, can't be jogged, zeroed,
        // or shown. The three axis-select buttons each toggle a PAIR, skipping any
        // disabled member: [X|A], [Y|B], [Z|C]. Row ORDER defines the pairs (see
        // the AxX..AxC indices in KflopLink); keep it as below.
        //
        //   Rotary=false -> the axis works in INCHES / IPM  (Cpu = counts/inch).
        //   Rotary=true  -> the axis works in DEGREES / deg-per-min (Cpu = cnt/deg).
        //
        // Z and C carry Cpu/MaxRate/accel = 0: those two are RESOLVED AT RUNTIME
        // from the knee/quill scalars above + the init's config id (so one build
        // is right for either init). Leave them 0 unless your machine has no
        // knee/quill swap, in which case fill them in like the other rows.
        public struct AxisCfg
        {
            public bool   Enabled;
            public char   Label;      // letter shown on the LCD
            public bool   Rotary;     // false = inch/IPM, true = degree/deg-per-min
            public int    JogCh;      // KFLOP channel to jog
            public int    DroSlot;    // coordinate DRO slot (UD_DRO + slot)
            public double Cpu;        // counts per inch (or per degree); 0 = runtime knee/quill
            public double MaxRate;    // IPM (or deg/min);                0 = runtime knee/quill
            public double JogAccel;   // counts/sec^2;                    0 = runtime knee/quill
            public double StepAccel;  // counts/sec^2;                    0 = runtime knee/quill
        }

        // The axis-enable TABLE (structure: rows, labels, channels, DRO slots,
        // enable flags, and the Z/C runtime markers) is COMPILED. Its rate/accel
        // NUMBERS, however, come from the loaded scalars (IpmXY, JogAccXY, DpmA,
        // ...) -- so BuildAxes() reads those fields when it runs. The static ctor
        // fills Axes with a compiled default; TuneConfig.Load() rebuilds it AFTER
        // applying pendant.conf. (C# runs every static field initializer before the
        // static ctor body, so BuildAxes sees the shipped scalar defaults there
        // regardless of declaration order; the loader then supplies the file values.)
        public static AxisCfg[] Axes;

        static Tune() { Axes = BuildAxes(); }

        internal static AxisCfg[] BuildAxes()
        {
            return new AxisCfg[]
            {
                new AxisCfg { Enabled=true,  Label='X', Rotary=false, JogCh=0, DroSlot=0, Cpu=CpiXY, MaxRate=IpmXY, JogAccel=JogAccXY, StepAccel=StepAccXY },
                new AxisCfg { Enabled=true,  Label='A', Rotary=true,  JogCh=4, DroSlot=3, Cpu=CpdA,  MaxRate=DpmA,  JogAccel=JogAccA,  StepAccel=StepAccA  },
                new AxisCfg { Enabled=true,  Label='Y', Rotary=false, JogCh=1, DroSlot=1, Cpu=CpiXY, MaxRate=IpmXY, JogAccel=JogAccXY, StepAccel=StepAccXY },
                new AxisCfg { Enabled=false, Label='B', Rotary=true,  JogCh=5, DroSlot=4, Cpu=CpdB,  MaxRate=DpmB,  JogAccel=JogAccB,  StepAccel=StepAccB  },
                new AxisCfg { Enabled=true,  Label='Z', Rotary=false, JogCh=2, DroSlot=2, Cpu=0,     MaxRate=0,     JogAccel=0,        StepAccel=0         },
                new AxisCfg { Enabled=true,  Label='C', Rotary=false, JogCh=3, DroSlot=5, Cpu=0,     MaxRate=0,     JogAccel=0,        StepAccel=0         },
            };
        }

        // ===== 3b. MODE ENABLE TABLE ==========================================
        // The three MODE buttons each toggle a PAIR, exactly like the axis-select
        // buttons. Row ORDER defines the pairs and MUST match the ModeCode values
        // in Bridge (Step=0, Cont=1, F=2, Vel=3, CRate=4, Sovr=5):
        //
        //     S  <-> Vv     (ModeStep button)   -- step jog   / velocity jog
        //     Cs <-> C%     (ModeCont button)   -- cont. jog  / cont-rate dial
        //     F% <-> S%     (ModeF button)      -- feed ovr   / spindle ovr
        //
        // Enabled=false takes that mode OUT of its button's toggle: the button then
        // lands on the enabled half every press. If BOTH halves of a pair are off,
        // that mode button does nothing at all.
        //
        // SAFETY: at least one JOG mode (S, Vv, or Cs) must stay enabled, or you
        // could not jog the machine from the pendant. The bridge checks this at
        // startup and refuses to run with a clear message rather than leaving you
        // stranded at the machine.
        public struct ModeCfg
        {
            public bool   Enabled;
            public string Label;      // short name, for startup/diagnostic messages
        }

        // Indexed by ModeCode: 0=S, 1=Cs, 2=F%, 3=Vv, 4=C%, 5=S%
        public static readonly ModeCfg[] Modes =
        {
            new ModeCfg { Enabled=true, Label="S"  },   // 0 step jog
            new ModeCfg { Enabled=true, Label="Cs" },   // 1 continuous jog
            new ModeCfg { Enabled=true, Label="F%" },   // 2 feed override
            new ModeCfg { Enabled=true, Label="Vv" },   // 3 velocity jog
            new ModeCfg { Enabled=true, Label="C%" },   // 4 continuous-rate dial
            new ModeCfg { Enabled=true, Label="S%" },   // 5 spindle override
        };

        // ===== 3c. ACTION-BUTTON ENABLE TABLE =================================
        // The hold-then-EN function buttons. Enabled=false makes the button FULLY
        // INERT: holding it shows nothing on the LCD and the EN tap does nothing.
        // (A button that prompts and then silently ignores you is worse than one
        // that plainly does nothing.)
        //
        // EN and E-STOP are deliberately NOT in this table -- they are never
        // gateable. EN is the execute modifier the whole grammar depends on, and
        // E-Stop must always be able to stop the machine.
        public struct ButtonCfg
        {
            public bool   Enabled;
            public string Label;      // short name, for diagnostic messages
        }

        // Indexed by BtnCode in Bridge: 0=F1, 1=F2, 2=F3, 3=Spindle, 4=Start, 5=Stop
        public static readonly ButtonCfg[] Buttons =
        {
            new ButtonCfg { Enabled=true,  Label="F1 ZERO"  },   // 0
            new ButtonCfg { Enabled=true,  Label="F2 GOTOZ" },   // 1
            new ButtonCfg { Enabled=true,  Label="F3 LOCK"  },   // 2
            new ButtonCfg { Enabled=true,  Label="SPINDLE"  },   // 3
            new ButtonCfg { Enabled=true,  Label="START"    },   // 4
            new ButtonCfg { Enabled=true,  Label="STOP"     },   // 5
        };

        // Process name of KMotionCNC (no ".exe"). The bridge waits for this to be
        // running before it connects -- so it sits quietly at WAIT/CNC instead of
        // flapping when you use the PC without a milling session.
        public const string CncProcessName = "KMotionCNC";

        // ===== AUTO-START ====================================================
        // Read by pendant\autostart\run-pendant.ps1 BEFORE the bridge launches --
        // the wrapper greps this line, so changing it needs no rebuild. It stays
        // COMPILED here (NOT in pendant.conf) precisely so that grep keeps working.
        //   true  = the PendantBridge logon task starts the bridge automatically
        //   false = the task fires but exits immediately; start it by hand instead
        // The desktop "Restart" icon passes -Force and ignores this, so a manual
        // start always works. The OS-level off switch is still
        // Disable-ScheduledTask -TaskName PendantBridge.
        public const bool AutoStart = true;

        // ----- Auto-start gate timing -----------------------------------------
        // The bridge is launched at logon and waits: first for the pendant, then
        // for the KFLOP to power up, then for an init to publish its config id.
        // These are the retry/poll intervals in milliseconds. Bigger = calmer
        // polling, slightly slower pickup.
        public const int GatePendantRetryMs = 1000;
        public const int GateKflopPollMs    =  750;
        public const int GateConfigPollMs   =  300;

        // ===== 1b. PENDANT INPUT WATCHDOG (safety) ============================
        // Guards the USB-drop RUNAWAY: under per-command dynamics a continuous jog
        // (Vv/Cs) keeps moving at its last commanded velocity until a new command
        // stops it. The normal wheel-idle stop (Bridge.ContIdleStopMs) lives inside
        // HandleJog, which only runs when a pendant report arrives -- so if the
        // pendant USB drops mid-jog, reports stop, HandleJog never runs, and the
        // axis would run away. This watchdog runs in the main loop on the NULL path
        // too: if no valid report has arrived for JogWatchdogMs while a jog is
        // active, it stops that jog (the KFLOP is a SEPARATE USB device, so the
        // bridge can still command the stop). If the stall persists to
        // PendantDeadMs the pendant is treated as lost and the bridge exits clean
        // for Task Scheduler to relaunch and re-open it (mirrors KFLOP link-loss);
        // a brief glitch that recovers before then resets the clock -- no relaunch.
        //
        // SAFETY VALUE -- Jim to confirm at the machine. 150ms bounds runaway to
        // ~0.125" at the 50 IPM X cap (far less on knee/quill), with ~10x margin
        // over the normal report gap (reports stream every ~8-16ms), so healthy
        // jogging never trips it. Raise if you ever see a nuisance stop; lower to
        // tighten the runaway bound.
        public const int JogWatchdogMs = 150;
        // How long a total input stall persists before the pendant is declared lost
        // and the bridge exits to relaunch/re-open it. Well above JogWatchdogMs so a
        // transient EMI/hub blip that recovers doesn't force a full relaunch.
        public const int PendantDeadMs = 2000;

        // ===== 2. JOG SPEED CAPS (IPM) =========================================
        // Max jog speed per axis. Vv reaches these at a brisk spin; Cs runs at a
        // fraction (CsSetPoint). X gets rough above ~15 (I-only servo).
        // [pendant.conf: IpmXY, IpmKnee, IpmQuill]
        public static double IpmXY    = 50.0;
        public static double IpmKnee  = 9.0;    // 2026-07-25: -10% from 10.0 per Jim (knee jog feel)
        public static double IpmQuill = 25.0;

        // ===== 2b. GOTOZ (F2) -- return-to-work-origin move ====================
        // GOTOZ raises Z to GotozZClearInch above work zero (retract ONLY, it never
        // descends), then sends X & Y to work zero. Feedrates are inch/min.
        // [pendant.conf: GotozZClearInch, GotozIpmZ, GotozIpmXY]
        public static double GotozZClearInch = 0.5;   // safe Z clearance above work zero
        public static double GotozIpmZ       = 5.0;   // Z retract feedrate
        public static double GotozIpmXY      = 10.0;  // X/Y return feedrate

        // ===== 3. VELOCITY (Vv) -- "Speed Change Sensitivity" ==================
        // Wheel speed (detents/sec) that commands 100% of the cap. LOWER = full
        // speed with a gentler spin (i.e. more sensitive). ~150 = brisk-natural.
        // [pendant.conf: FullScaleDPS]
        public static double FullScaleDPS = 150.0;
        // Smoothing of the wheel-speed estimate (seconds). Bigger = smoother/laggier.
        // [pendant.conf: WheelTauSec]
        public static double WheelTauSec  = 0.10;

        // ===== 4. CONTINUOUS (Cs) + C% -- "Slow Jog Rate" ======================
        // Cs is a deliberately slow, steady rate (per the VistaCNC manual), NOT
        // Vv's max. CsSetPoint = that rate at C%=100%, as a fraction of the cap;
        // C% dials it 0..200% around this point.
        // [pendant.conf: CsSetPoint, CsPctPerDetent, CsRateFloor, CsRateCeil, CsStepsToStart]
        public static double CsSetPoint     = 0.25;
        public static double CsPctPerDetent = 0.05;   // C% additive step (5%/detent)
        public static double CsRateFloor    = 0.0;    //   0%
        public static double CsRateCeil     = 2.0;    // 200%
        // "Steps to Delay Start": Cs ignores this many wheel detents before it
        // starts moving, so a stray nudge won't lurch the table. 0 = no delay.
        public static int    CsStepsToStart = 2;

        // ===== 5. STEP =========================================================
        // Step sizes cycled by En+wheel in Step mode. List ONLY what you want
        // (1..9 entries); first = default. The pendant cycles exactly these.
        // [pendant.conf: StepSizes]
        public static double[] StepSizes = { 0.0010, 0.0100, 0.0005 };
        // Feed rate (IPM) used to execute a step move. Steps are small so this is
        // usually reached only on the larger sizes; keep it modest.
        // [pendant.conf: StepJogFeedIpm]
        public static double StepJogFeedIpm = 12.0;
        // Step-move acceleration per axis (counts/sec^2). Bigger = snappier steps.
        // Keep the KNEE at/under ~90000 or it hammers.
        // [pendant.conf: StepAccXY, StepAccKnee, StepAccQuill]
        public static double StepAccXY    = 120000.0;
        public static double StepAccKnee  =  90000.0;
        public static double StepAccQuill = 120000.0;

        // ===== 6. JOG RAMP (Vv/Cs acceleration) ================================
        // How briskly continuous jog ramps to speed (counts/sec^2). Keep each
        // below its axis's machine-rated accel (knee ~100000, quill ~270000).
        // [pendant.conf: JogAccXY, JogAccKnee, JogAccQuill]
        public static double JogAccXY    = 400000.0;
        public static double JogAccKnee  =  90000.0;
        public static double JogAccQuill = 250000.0;

        // ===== 7. HALF-SPEED (En held in Vv/Cs) ================================
        // [pendant.conf: HalfSpeedMul]
        public static double HalfSpeedMul = 0.5;

        // ===== 8. DISPLAY ======================================================
        // DRO decimal places on the LCD: 2, 3, or 4.
        // [pendant.conf: DroDecimals]
        public static int DroDecimals = 4;

        // ===== 8b. SPINDLE OVERRIDE (S%) =======================================
        // Faithful to VistaCNC vc-p4s.hal (halui.spindle-override.scale 0.001):
        // 0.001 fraction added per MPG detent = 0.1%/detent (100 detents/rev ->
        // ~10% per full turn). 100% (1.0) = the commanded spindle speed. VistaCNC
        // sets no min/max in the package (that clamp lives in the LinuxCNC .ini,
        // which doesn't apply through the KFLOP), so the range is a free choice.
        // [pendant.conf: SsoStepPerDetent, SsoMin, SsoMax, SsoDefault]
        public static double SsoStepPerDetent = 0.001;   // fraction per detent (0.1%)
        public static double SsoMin           = 0.0;     //   0%
        public static double SsoMax           = 2.0;     // 200%
        public static double SsoDefault       = 1.0;     // 100% = commanded speed

        // ===== 9. DEFERRED -- NOT WIRED YET ====================================
        // These match config items the original exposed, but depend on features
        // we haven't built (spindle control, absolute FRO set, MDI/fixtures).
        // Listed here so the config is complete; they do NOTHING until wired.
        //
        // F% quick-select presets (FRO %). Needs an absolute-FRO-set command.
        public static readonly double[] FroPresets = { 25, 50, 100, 150 };
        // S% quick-select presets (spindle override %). Needs spindle wiring.
        public static readonly double[] SpindlePresets = { 50, 75, 100 };
        // Spindle M-codes for En+Spindle (on/off). Needs spindle policy.
        public const int SpindleOnMCode  = 3;   // M3
        public const int SpindleOffMCode = 5;   // M5

        // ===== 10. LCD MESSAGES (each line shows up to 8 characters) ===========
        // Banner / status text the BRIDGE shows on the pendant LCD. Each line is
        // padded or truncated to 8 chars, so keep them short. Edit freely.
        //
        // NOTE: the pendant firmware's own "LinuxCNC----" idle screen shows
        // whenever the bridge isn't writing frames -- before launch, after exit,
        // and (by design) while the bridge waits for the KFLOP to power up. That
        // text lives in the pendant firmware and can't be changed here. The bridge
        // leaves it up until the controller is present, then replaces it with the
        // WaitCnc banner below, then WaitInit.

        // -- startup gate banners (line 1 / line 2) --
        // (The "waiting for KFLOP" state deliberately draws nothing, leaving the
        //  firmware's LinuxCNC idle screen up until the controller is present.)
        public const string WaitCncL1   = "Waiting";   // waiting for KMotionCNC to be running
        public const string WaitCncL2   = "for CNC";
        public const string WaitInitL1  = "Waiting";   // waiting for an init (config id)
        public const string WaitInitL2  = "for init";

        // -- error / link banners (line 1 / line 2) --
        public const string ConnFailL1  = "CONNECT";   // KFLOP connect failed
        public const string ConnFailL2  = "FAIL";
        public const string LinkLostL1  = "LINK";      // KFLOP link lost mid-session
        public const string LinkLostL2  = "LOST";
        public const string ConfigErrL1 = "CONFIG";    // pendant.conf missing/malformed -> refuse to start
        public const string ConfigErrL2 = "ERR";

        // -- in-run status words (line 2 only; line 1 keeps the DRO) --
        public const string StatusNoLink  = "NO LINK";  // link down while running
        public const string StatusHold    = "HOLD";     // feed-hold active
        public const string StatusBusy    = "BUSY";     // machine moving (not idle)
        public const string StatusZeroed  = "ZEROED";   // zero succeeded
        public const string StatusZeroErr = "ZERO ER";  // zero failed
        public const string StatusGotoz   = "GOTOZ";     // go-to-zero move issued (post-move flash)

        // Init-load banner (full screen for ~2s when a new init is loaded mid-session).
        // Line 1 = the init name (by identity var 56: 1 Standard / 2 Knee Z / 3 PCB),
        // line 2 = InitLoadedL2. Each fits the 8-char LCD line.
        public const string InitLoadedL2   = "Loaded";
        public const string InitNameStd    = "Standard";  // identity 1
        public const string InitNameKnee   = "Knee Z";    // identity 2
        public const string InitNamePcb    = "PCB";       // identity 3
        public const string InitNameUnknown= "Init?";     // identity 0/unknown (init without var 56)

        // ===== 10b. Button prompt / label text (line 1 + line 2), all editable.
        //           HOLD a function button to preview these; TAP EN to execute. =====
        public const string ZeroPrefix     = "ZERO ";    // F1: prefix + axis letter + "?"
        public const string GotozPrompt    = "GOTOZ?";   // F2: while held
        public const string MachLbl        = "Control";  // F3: line 1 (enable/disable the controls)
        public const string MachPromptLock = "Lock ON?"; // F3 held & controls ENABLED  -> offer to lock
        public const string MachPromptUnlk = "Lck OFF?"; // F3 held & controls DISABLED -> offer to unlock
        public const string MachLockOn     = "Lock ON";  // controls DISABLED (locked)  -- line 2
        public const string MachLockOff    = "Lock OFF"; // controls ENABLED  (unlocked)-- line 2
        public const string StartPrompt    = "START?";   // Start button, machine idle
        public const string ResumePrompt   = "RESUME?";  // Start button, feed-held
        public const string HoldPrompt     = "HOLD?";    // Start button, running
        public const string StopPrompt     = "STOP?";    // Stop button
        public const string SpindleOvLbl   = "Spindle";  // S% override readout, line 1
        public const string SpindleLbl     = "SPINDLE";  // spindle button held, line 1
        public const string SpindleOnP     = "START?";   // spindle off -> offer start
        public const string SpindleOffP    = "STOP?";    // spindle on  -> offer stop
    }
}
