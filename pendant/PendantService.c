/*
 * PendantService.c  -  iMach P4-S -> KFLOP pendant bridge, Stage 2
 *
 * Persistent KFLOP thread that OWNS the PC <-> KMotionCNC command channel
 * (persist.UserData[PC_COMM_PERSIST]). Being the single owner means every
 * channel command is serialized. Each pass it:
 *   1.  reads the live work (green) DROs from KMotionCNC and parks them in
 *       UserData (exact, user units, already reflect the active fixture);
 *   2.  parks the active fixture index (for the LCD);
 *   2b. parks the live spindle enable state (of the ACTIVE spindle) for the LCD;
 *   3.  services a pending ZERO request (sets that axis's fixture/work offset);
 *   4.  services a pending COMMAND request (cycle start / e-stop / halt / MCode /
 *       feed-override-increment / spindle on-off / spindle-speed-override);
 *   5.  bumps a heartbeat.
 *
 * --------------------------------------------------------------------------
 * CRITICAL LAYOUT RULE - why TMP is where it is (root-cause fix, June 2026):
 *
 *   KflopToKMotionCNCFunctions.c uses the macro TMP as a *scratch buffer*, not
 *   a single cell. GetDROs() has KMotionCNC write SIX doubles into
 *   persist double-index TMP..TMP+5; GetFixtureIndex()/GetOriginOffset() write
 *   TMP. So TMP..TMP+5 is clobbered on EVERY helper call.
 *
 *   Earlier versions set TMP = our data base, so GetDROs wrote axis values on
 *   top of ZERO_REQ (TMP+2) and DRO[0]/DRO[1] (TMP+4/+5). That caused BOTH the
 *   DRO flicker-to-zero AND the "phantom zero" (Z's DRO landing in ZERO_REQ and
 *   tripping the zero handler). The mis-labeled "external stomp" was this.
 *
 *   FIX: TMP gets its OWN dedicated 6-double region (30..35) that overlaps
 *   nothing. Our data lives at 36..49. The PC_COMM channel is at cells 100..107
 *   (double-index 50..53). No region overlaps another.
 *
 *   persist.UserData map (double-index N <-> cells [N*2, N*2+1]):
 *     30..35  TMP scratch (cells 60..71)  - helper-owned; we never persist here
 *     36..49  our data     (cells 72..99)
 *     50..53  PC_COMM      (cells 100..107)
 *     54      config id    (init -> bridge)
 *     55      spindle state (this service -> bridge)
 *     56      TP_HOLD_REQ  (init -> this service; init asks for exclusive
 *                           PC_COMM ownership around SetTPParameter block)
 *     57      TP_HOLD_ACK  (this service -> init; acknowledged, DRO polling paused)
 * --------------------------------------------------------------------------
 *
 * UserData contract (MUST match KflopLink.cs):
 *   IN  UD_CMD_REQ  : bridge command id (CMD_* below), 0 = idle. Acked with 0.
 *   IN  UD_CMD_ARG  : command argument (MCode number, FRO factor; for
 *                     CMD_SPINDLE the desired spindle state !=0 on/0 off; for
 *                     CMD_SSO the absolute override fraction, 1.0 = 100%).
 *   OUT UD_HEARTBEAT: incremented every pass.
 *   OUT UD_DRO..+5  : work (green) DROs X..C, user units.
 *   OUT UD_FIXTURE  : active fixture index (1 = G54, ...).
 *   OUT UD_CMD_STAT : last command result (0 = OK, negative = KMotionCNC error).
 *   OUT UD_SPINDLE  : live enable bit of the ACTIVE spindle; 1 = on, 0 = off.
 *
 * Lifecycle: bridge loads this ONCE with ExecuteProgram(thread,file,false);
 * main() loops forever; bridge stops it with KillProgramThreads(thread).
 */

#include "KMotionDef.h"

/* ---- dedicated scratch for KflopToKMotionCNCFunctions.c (GetDROs uses
 *      TMP..TMP+5). MUST NOT overlap the data block (36..49) or PC_COMM. ---- */
#define TMP           30           /* double-idx 30..35 -> cells 60..71 */

/* ---- our persistent data block (double-idx 36..49 -> cells 72..99) ---- */
#define UD_HEARTBEAT  36
#define UD_DRO        39           /* 39..44 (X..C) */
#define UD_FIXTURE    46
#define UD_CMD_REQ    47
#define UD_CMD_ARG    48
#define UD_CMD_STAT   49
#define UD_SPINDLE    55           /* out: live spindle enable; 50..53 PC_COMM, 54 config-id, 55 first free */

/* ---- machine I/O ---- */
/* Spindle ENABLE bits -- exactly the bits KMotionCNC's M3/M4 set and M5/M30/Stop
 * clear. The pendant toggles one of these DIRECTLY. It never writes a speed DAC
 * and never issues an M or S code, so a spindle it stops keeps the speed last
 * commanded by M3Snnn and restarts at that same speed. */
#define SPINDLE_BIT_BIG  151       /* big OEM spindle VFD enable (dir = bit 150) */
#define SPINDLE_BIT_HS   156       /* high-speed spindle VFD enable              */

/* RAW persist.UserData[] cell -- NOT a double index -- where the loaded init
 * publishes which spindle is live: 0 = big OEM, 1 = high-speed. Sits clear of
 * this file's double-index map (which tops out at 66 -> cells 132/133) and of
 * KMotionCNC's PC_COMM / bulk-status cells (100..107). MUST match the value
 * written by kflop-init\JPB - *.c and read by m-codes\Spindle*.c. */
#define SPINDLE_SEL_CELL 150

/* bridge command ids (our own; mapped to PC_COMM_* below) */
#define CMD_NONE      0
#define CMD_EXECUTE   1   /* cycle start (run loaded G-code)            */
#define CMD_ESTOP     2   /* KMotionCNC full E-stop                     */
#define CMD_HALT      3   /* stop the interpreter                       */
#define CMD_MCODE     4   /* arg = MCode number to execute              */
#define CMD_FRO_INC   5   /* arg = multiplicative feed-override factor  */
#define CMD_SPINDLE   6   /* arg = desired spindle enable (!=0 on, 0 off) */
#define CMD_SSO       7   /* arg = absolute spindle-speed override fraction (1.0 = 100%) */
#define CMD_ZERO      8   /* arg = coord axis 0..5 (X..C) to zero via native Set */
#define CMD_GOTOZ     9   /* GOTOZ (F2): retract Z to clearance, then X&Y -> work zero */
#define CMD_MACHINE   10  /* F3: toggle machine (all axes) enable/disable            */

/* GOTOZ parameters, written once by the bridge (from Tune) at connect.
 * Fresh cells (double-idx 62..64 -> 124..129), clear of everything above. */
#define UD_GOTOZ_CLEAR 62  /* safe Z height above work zero (inch)  */
#define UD_GOTOZ_IPMZ  63  /* Z retract feedrate (inch/min)         */
#define UD_GOTOZ_IPMXY 64  /* X/Y return feedrate (inch/min)        */
#define UD_MACHINE     65  /* out: live machine (axes) enable; 1 = on, 0 = off */

/* PC_COMM channel serialization handshake (Tom Kerekes 2026-07-24). The
 * KMotionCNC command channel at cells 100..107 (double-idx 50..53) has ONE
 * mailbox and no arbitration between KFLOP threads. This service polls it
 * ~10 Hz via GetDROs/GetFixtureIndex on thread 7. When an init on thread 4
 * calls SetTPParameter, its command races the pendant's — whichever writes
 * cell 100 second wins, and the loser's wait loop sees the ack (cell 100
 * cleared to 0) as a "success" for a command KMotionCNC never saw.
 *
 * Protocol: before its TP block the init sets TP_HOLD_REQ=1 and waits for
 * TP_HOLD_ACK=1. This service checks TP_HOLD_REQ at the TOP of every pass;
 * if set, ack and wait for it to clear (any command it has in flight this
 * pass is guaranteed complete). Init clears TP_HOLD_REQ when its TP block
 * is done; this service clears TP_HOLD_ACK and resumes polling. DRO updates
 * pause for the ~0.6s the TP block takes, then resume normally.
 */
#define TP_HOLD_REQ    56  /* in : init asks for exclusive PC_COMM ownership */
#define TP_HOLD_ACK    57  /* out: this service granted; init may proceed    */

/* Included by BASENAME: the build deploys KflopToKMotionCNCFunctions.c next to this
 * file (and the exe) in KMotion\Release64, so the KFLOP C compiler resolves it
 * against THIS file's own folder. A "../shared/..." path does NOT work here -- this
 * file is deployed away from the repo's shared/ folder, and the compiler does not
 * search KMotion's "C Programs" for it either (verified at the machine: both failed
 * to load). (The inits keep "../shared/..." because they compile in place from their
 * repo folder, not deployed.) The C# build does not compile this file; KFLOP compiles
 * it when the bridge loads it at connect. */
#include "KflopToKMotionCNCFunctions.c"

/* Global heartbeat counter (also advanced by MDI_KeepAlive during PC waits). */
double beat = 0.0;

/* ---- which spindle is live? ------------------------------------------------
 * Reads the selector the loaded init published. 1 = high-speed spindle;
 * anything else = big OEM spindle, which deliberately matches the else-branch
 * of the M-code handlers (m-codes\Spindle_S.c etc.) so a garbage or unwritten
 * cell can never select the high-speed spindle by accident.
 *
 * Resolved LOCALLY on every use rather than cached, so switching inits takes
 * effect immediately and stays correct even if the bridge is disconnected. */
int ActiveSpindleBit(void)
{
    return (persist.UserData[SPINDLE_SEL_CELL] == 1) ? SPINDLE_BIT_HS : SPINDLE_BIT_BIG;
}

/* Tracks the last machine-enable state written to the KMotionCNC screen label
 * (Var 172), so we only push a DROLabel update when it actually flips. -1 forces
 * a write on the first pass so the screen shows the correct state at startup. */
int lastMachLabel = -1;

/* Gather-buffer word offset where MDI_KeepAlive parks the G-code string for
 * KMotionCNC to read. Guard it: if an include already defines it we use that;
 * otherwise this fallback (well clear of typical Step-Response captures at 0). */
#ifndef GATH_OFF
#define GATH_OFF 1024
#endif

/* Heartbeat-friendly MDI. Issues a G-code line to KMotionCNC's interpreter but,
 * unlike the stock blocking MDI(), keeps UD_HEARTBEAT advancing on every time
 * slice while KMotionCNC runs the line -- so the bridge's link-loss watchdog
 * does NOT false-trip on a slow PC round-trip (which is what crashed the link
 * when the zero used the stock MDI()). A 3 s timeout guard prevents any hang.
 * Returns 0 on success, <0 on interpreter error, -999 on timeout. */
int MDI_KeepAlive(char *s)
{
    char  *p = (char *)gather_buffer + GATH_OFF * sizeof(int);
    double tout;
    int    result;

    do { *p++ = *s++; } while (s[-1]);                 /* copy G-code to gather buffer */

    persist.UserData[PC_COMM_PERSIST + 1] = GATH_OFF;    /* MDI arg = gather offset */
    persist.UserData[PC_COMM_PERSIST]     = PC_COMM_MDI;  /* issue -- do NOT block  */

    tout = Time_sec() + 3.0;
    do
    {
        WaitNextTimeSlice();
        beat += 1.0;                                   /* keep the heartbeat alive */
        SetUserDataDouble(UD_HEARTBEAT, beat);
        result = persist.UserData[PC_COMM_PERSIST];
        if (Time_sec() > tout) return -999;            /* PC wedged -> bail (no hang) */
    } while (result > 0);

    return result;                                     /* 0 = ok, <0 = interpreter error */
}

/* Heartbeat-friendly DoPC. Same shape as MDI_KeepAlive above, but for the no-arg
 * PC_COMM commands (ESTOP, HALT, EXECUTE). Bench 2026-07-19: raw DoPC(PC_COMM_ESTOP)
 * hangs thread 7 -- KMotionCNC stops servicing PC_COMM once it enters its own
 * E-stop state, so the call never returns; heartbeat stops; the bridge marks the
 * link lost and exits; KMotionCNC needs a full close + init reload to recover.
 * This wrapper ticks UD_HEARTBEAT via WaitNextTimeSlice() while it waits, and
 * gives up after 3 s so a wedged PC no longer takes down the service.
 * Returns 0 on success, <0 on interpreter error, -999 on timeout. */
int DoPC_KeepAlive(int cmd)
{
    double tout;
    int    result;

    persist.UserData[PC_COMM_PERSIST + 1] = 0;         /* no arg for these commands */
    persist.UserData[PC_COMM_PERSIST]     = cmd;       /* issue -- do NOT block     */

    tout = Time_sec() + 3.0;
    do
    {
        WaitNextTimeSlice();
        beat += 1.0;                                   /* keep the heartbeat alive */
        SetUserDataDouble(UD_HEARTBEAT, beat);
        result = persist.UserData[PC_COMM_PERSIST];
        if (Time_sec() > tout) return -999;            /* PC wedged -> bail (no hang) */
    } while (result > 0);

    return result;                                     /* 0 = ok, <0 = KMotionCNC error */
}

main()
{
    int    FixtureIndex, req, axis, cmd, res, i, ok, attempt;
    int    zseq, zdone;                 /* seq-tagged zero handshake */
    double arg, OldOffset, NewOffset, CheckOffset, diff;
    double dro[6];
    char   zbuf[32];
    char   axisLetters[] = "XYZABC";

    SetUserDataDouble(UD_CMD_REQ, 0.0);

    for (;;)
    {
        /* TP HOLD handshake (Tom Kerekes 2026-07-24). If an init is about to
         * do a SetTPParameter block, wait it out — this service must not
         * issue any PC_COMM command while the init has one in flight, or
         * the two commands would race the single mailbox at cells 100..107.
         * Checked at the TOP of the pass so that anything the previous pass
         * issued (its GetDROs / GetFixtureIndex) is already complete before
         * we ack. Init clears TP_HOLD_REQ when its block is done. */
        if ((int)GetUserDataDouble(TP_HOLD_REQ))
        {
            SetUserDataDouble(TP_HOLD_ACK, 1.0);
            while ((int)GetUserDataDouble(TP_HOLD_REQ))
            {
                WaitNextTimeSlice();
                beat += 1.0;               /* keep heartbeat alive during the hold */
                SetUserDataDouble(UD_HEARTBEAT, beat);
            }
            SetUserDataDouble(TP_HOLD_ACK, 0.0);
        }

        /* 1 + 2. publish live work DROs and active fixture.
         *        (GetDROs / GetFixtureIndex scribble on TMP..TMP+5 = 30..35,
         *         which is clear of everything below.) */
        GetDROs(&dro[0], &dro[1], &dro[2], &dro[3], &dro[4], &dro[5]);
        for (i = 0; i < 6; i++)
            SetUserDataDouble(UD_DRO + i, dro[i]);

        GetFixtureIndex(&FixtureIndex);
        SetUserDataDouble(UD_FIXTURE, (double)FixtureIndex);

        /* 2b. publish the live enable state of the ACTIVE spindle for the LCD
         *     prompt. Cheap local read of the commanded output bit; reflects
         *     whatever last set it (KMotionCNC M3/M5 or our own CMD_SPINDLE).
         *     Reading the ACTIVE spindle's bit is what makes the pendant ask the
         *     RIGHT question (START? vs STOP?) under every init -- previously it
         *     always watched 156, so with the big spindle running it reported
         *     'off' and offered START?. */
        SetUserDataDouble(UD_SPINDLE, (double)ReadBit(ActiveSpindleBit()));

        /* 2c. publish the live machine (axes) enable state for the LCD prompt and
         *     the KMotionCNC screen indicator. chan[0].Enable reflects the F3 toggle
         *     (all axes move together); 1 = machine on, 0 = off. */
        SetUserDataDouble(UD_MACHINE, (double)chan[0].Enable);

        /* 2d. drive the KMotionCNC screen "Control" indicator (DROLabel Var 172).
         *     Same live state as UD_MACHINE, but written as text only WHEN IT FLIPS
         *     (change-detected, so no needless status-bus traffic). "UNLOCKED" =
         *     controls enabled; "LOCKED" = controls disabled -- matching the pendant
         *     LCD's Lock OFF / Lock ON wording. Uses the standard DROLabel gather
         *     offset (1000), clear of MDI_KeepAlive's GATH_OFF (1024). */
        {
            int en = chan[0].Enable ? 1 : 0;
            if (en != lastMachLabel)
            {
                lastMachLabel = en;
                DROLabel(1000, 172, en ? "UNLOCKED" : "LOCKED");
            }
        }

        /* 3. (ZERO now arrives via the CMD channel as CMD_ZERO -- see the command
         *    dispatch below. The old dedicated ZERO_REQ/ZERO_VAL/ZERO_STAT channel
         *    was removed because its repeated bridge->KFLOP writes did not land
         *    reliably, while the CMD channel never misses.) */


        /* 4. pending COMMAND request (0 = idle) */
        cmd = (int)GetUserDataDouble(UD_CMD_REQ);
        if (cmd != CMD_NONE)
        {
            arg = GetUserDataDouble(UD_CMD_ARG);
            res = -1;
            if      (cmd == CMD_EXECUTE) res = DoPC(PC_COMM_EXECUTE);
            else if (cmd == CMD_ESTOP)   res = DoPC_KeepAlive(PC_COMM_ESTOP);
            else if (cmd == CMD_HALT)    res = DoPC(PC_COMM_HALT);
            else if (cmd == CMD_MCODE)   res = DoPCInt(PC_COMM_MCODE, (int)arg);
            else if (cmd == CMD_FRO_INC) res = DoPCFloat(PC_COMM_SET_FRO_INC, (float)arg);
            else if (cmd == CMD_SPINDLE)
            {
                /* Direct enable-bit toggle - NOT an M-code. Unconditional and
                 * instant, no dependence on the interpreter. Mirrors exactly what
                 * KMotionCNC's M3 (set) / M5 (clear) do to this bit.
                 *
                 * Acts on the ACTIVE spindle only, and touches the ENABLE BIT
                 * ALONE -- never the speed DAC, never the direction bit. So the
                 * spindle restarts turning the same way, at the same speed, as
                 * when it was stopped. By design the pendant can only stop a
                 * running spindle and restart a previously running one. */
                int bit = ActiveSpindleBit();
                if (arg != 0.0) SetBit(bit);
                else            ClearBit(bit);
                res = 0;
            }
            else if (cmd == CMD_SSO)
            {
                /* Absolute spindle-speed override. arg is the fraction the pendant
                 * owns (1.0 = 100% = commanded S); KMotionCNC re-applies it to the
                 * S->DAC output. Set-only (KMotionCNC has no read-SSO), benign,
                 * twin of the feed-override path -- NOT the interpreter. */
                res = DoPCFloat(PC_COMM_SET_SSO, (float)arg);
            }
            else if (cmd == CMD_ZERO)
            {
                /* Zero the axis in arg (coord 0..5 = X..C) via KMotionCNC's native
                 * Set (= the on-screen Set button; PC_COMM_SET_X+axis, fire-once).
                 * Routed through this RELIABLE CMD channel because the separate
                 * ZERO_REQ/ZERO_VAL channel's repeated writes did not land. The CMD
                 * machinery below acks CMD_STAT and clears CMD_REQ, exactly like the
                 * spindle/S% paths that never miss. Requires "Zero Using Fixture
                 * Offsets" CHECKED so it targets the active fixture (G54). */
                axis = (int)arg;
                if (axis >= 0 && axis <= 5)
                {
                    res = DoPCFloat(PC_COMM_SET_X + axis, 0.0F);
                }
                else res = -1;
            }
            else if (cmd == CMD_GOTOZ)
            {
                /* GOTOZ (F2): arg selects the phase; the BRIDGE sequences them and
                 * gates the X/Y phase on Z having physically REACHED clearance (so a
                 * HOLD or E-stop mid-retract can never trigger the X/Y move).
                 *   phase 0 = retract Z to clearance (up ONLY; never descends)
                 *   phase 1 = move X & Y to work zero
                 * Each phase is ONE MDI (a 2nd MDI is refused while the 1st move
                 * runs, hence the bridge waits between them). Heartbeat-safe;
                 * honors feedhold / E-stop like any interpreter move. */
                if ((int)arg == 0)
                {
                    OldOffset = GetUserDataDouble(UD_GOTOZ_CLEAR);   /* clearance (inch) */
                    NewOffset = GetUserDataDouble(UD_GOTOZ_IPMZ);    /* Z feedrate (ipm) */
                    if (dro[2] < OldOffset - 0.0005)                 /* below -> retract */
                    {
                        sprintf(zbuf, "G90 G1 Z%.4f F%.2f", OldOffset, NewOffset);
                        res = MDI_KeepAlive(zbuf);
                    }
                    else res = 0;                                    /* already clear    */
                }
                else
                {
                    NewOffset = GetUserDataDouble(UD_GOTOZ_IPMXY);   /* X/Y feedrate     */
                    sprintf(zbuf, "G90 G1 X0 Y0 F%.2f", NewOffset);
                    res = MDI_KeepAlive(zbuf);
                }
            }
            else if (cmd == CMD_MACHINE)
            {
                /* F3 Machine ON/OFF: toggle enable on ALL axes. The bridge only
                 * sends this when the machine is idle. Knee is non-backdriveable so
                 * disabling is safe (no gravity drop). Re-enable at the current
                 * encoder position (EnableAxis) so there's no jump. */
                if (chan[0].Enable)                  /* currently ON -> turn OFF */
                {
                    for (i = 0; i < 6; i++) DisableAxis(i);
                    res = 0;
                }
                else                                 /* currently OFF -> turn ON */
                {
                    for (i = 0; i < 6; i++) EnableAxis(i);
                    res = 1;
                }
            }
            SetUserDataDouble(UD_CMD_STAT, (double)res);
            SetUserDataDouble(UD_CMD_REQ, (double)CMD_NONE);
        }

        /* 5. heartbeat + pacing (GetDROs already blocks on the ~10 Hz channel) */
        beat += 1.0;
        SetUserDataDouble(UD_HEARTBEAT, beat);
        Delay_sec(0.02);
    }
}
