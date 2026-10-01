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
#define UD_ESTOP_REQ   59  /* in : init raises while hardware E-stop (bit 143) is asserted; edge-relayed to DoPC(PC_COMM_ESTOP) */
#define UD_INIT_ID     58  /* in : loaded init identity (1 Std / 2 Knee Z / 3 HSS; negative while loading) */
#define UD_TP_BAD      68  /* out: 1 = KMotionCNC's Z/C counts-per-inch don't match the loaded init, or the init
                                   is no longer running (KMotionCNC's STOP kills threads 2-7) -> RELOAD INIT */
#define UD_THREADS     69  /* out: raw KFLOP ThreadActive mask (bit n = thread n running), for diagnostics */
#define INIT_THREAD    4   /* all three inits run on Thread 4 (KMotionCNC Tool Setup, M110-M112) */

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
int PC_Busy(void);

int MDI_KeepAlive(char *s)
{
    char  *p = (char *)gather_buffer + GATH_OFF * sizeof(int);
    double tout;
    int    result;

    if (PC_Busy()) return -998;                          /* earlier request unanswered */
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

/* ---- Non-blocking PC_COMM (2026-09-28) --------------------------------------
 * While KMotionCNC shows a modal dialog (Tool Setup, File Open, ...) it stops
 * answering PC_COMM. The stock DoPC() waits forever, so this thread froze, the
 * heartbeat stalled, and the bridge declared the link lost and restarted (the
 * pendant flash / bridge window on every Tool Setup). Every request now goes
 * through PC_Issue: it refuses to post while an earlier request is still
 * unanswered (so a late answer can never be confused with a new request, and a
 * stale button command can never fire when the dialog closes), and waits with
 * the heartbeat ticking, giving up after a timeout. Routine polls just skip a
 * pass; commands fail cleanly (-998 busy / -999 timeout -> pendant shows error).
 * The E-stop/Halt relay keeps using DoPC_KeepAlive, which posts regardless. */
#define PC_POLL_TOUT   0.5         /* DROs / fixture / scale check            */
#define PC_CMD_TOUT    3.0         /* pendant button commands                 */

/* ---- PC_COMM response-time measurement (2026-09-29, diagnostic) -------------
 * How long KMotionCNC takes to answer each request posted through PC_Issue --
 * including requests that timed out and were answered later -- so Tom can see
 * where it stops servicing PC_COMM (Tool Setup / File Open opening?).
 *   UD 70 = longest wait seen (s)       UD 71 = PC_COMM command of that wait
 *   UD 72 = number of waits over 0.5 s  UD 73 = write 1 to reset all three
 * Observe-only: nothing here changes how requests are issued. */
#define UD_PC_MAXWAIT   70
#define UD_PC_MAXCMD    71
#define UD_PC_SLOWCOUNT 72
#define UD_PC_RESET     73
double pcIssueT = 0.0;
int    pcIssueCmd = 0, pcPending = 0;

void PC_Answered(void)
{
    double dur;
    if (!pcPending) return;
    pcPending = 0;
    dur = Time_sec() - pcIssueT;
    if (dur > GetUserDataDouble(UD_PC_MAXWAIT))
    {
        SetUserDataDouble(UD_PC_MAXWAIT, dur);
        SetUserDataDouble(UD_PC_MAXCMD, (double)pcIssueCmd);
    }
    if (dur > 0.5) SetUserDataDouble(UD_PC_SLOWCOUNT, GetUserDataDouble(UD_PC_SLOWCOUNT) + 1.0);
}

int PC_Busy(void)
{
    if ((int)GetUserDataDouble(UD_PC_RESET))
    {
        SetUserDataDouble(UD_PC_MAXWAIT, 0.0); SetUserDataDouble(UD_PC_MAXCMD, 0.0);
        SetUserDataDouble(UD_PC_SLOWCOUNT, 0.0); SetUserDataDouble(UD_PC_RESET, 0.0);
    }
    if (persist.UserData[PC_COMM_PERSIST] > 0) return 1;   /* request posted, not yet answered */
    PC_Answered();                                         /* a late answer to a timed-out request */
    return 0;
}

int PC_Wait(double tout)
{
    int    result;
    double t_end = Time_sec() + tout;
    do
    {
        WaitNextTimeSlice();
        beat += 1.0;                                   /* keep the heartbeat alive */
        SetUserDataDouble(UD_HEARTBEAT, beat);
        result = persist.UserData[PC_COMM_PERSIST];
        if (Time_sec() > t_end) return -999;           /* no answer (dialog open?) */
    } while (result > 0);
    PC_Answered();
    return result;                                     /* 0 = ok, <0 = KMotionCNC error */
}

/* post cmd with an integer argument (persist+1); extra args must be set first */
int PC_Issue(int cmd, int arg, double tout)
{
    if (PC_Busy()) return -998;
    persist.UserData[PC_COMM_PERSIST + 1] = arg;
    pcIssueT = Time_sec(); pcIssueCmd = cmd; pcPending = 1;
    persist.UserData[PC_COMM_PERSIST]     = cmd;
    return PC_Wait(tout);
}

int PC_IssueFloat(int cmd, float f, double tout)
{
    return PC_Issue(cmd, *(int *)&f, tout);
}

int GetDROs_NB(double *d)
{
    int i;
    if (PC_Issue(PC_COMM_GET_DROS, TMP, PC_POLL_TOUT)) return 1;
    for (i = 0; i < 6; i++) d[i] = GetUserDataDouble(TMP + i);
    return 0;
}

int GetFixtureIndex_NB(int *FixtureIndex)
{
    if (PC_Busy()) return 1;
    persist.UserData[PC_COMM_PERSIST + 2] = 1;         /* number of Vars    */
    persist.UserData[PC_COMM_PERSIST + 3] = TMP;       /* persist offset    */
    if (PC_Issue(PC_COMM_GET_VARS, 5220, PC_POLL_TOUT)) return 1;
    *FixtureIndex = (int)GetUserDataDouble(TMP);
    return 0;
}

/* ---- RELOAD INIT check (2026-09-27) ----------------------------------------
 * The inits set KMotionCNC's Trajectory Planner counts-per-inch for Z and C at
 * load time (SetTPParameter): Standard puts the quill on Z, Knee Z / HSS put the
 * knee on Z. Those values live only in KMotionCNC's memory, so if KMotionCNC is
 * restarted while the init keeps running on the KFLOP, it reverts to the values
 * in its config file (Standard's) until the init is loaded again -- and with the
 * knee driven at the quill's scale every Z move is ~42% of what was programmed
 * (2026-09-27, init then named PCB: holes drilled shallow after a KMotionCNC restart). The screen
 * even still said "PCB init LOADED" (the init repaints it on reconnect).
 *
 * Once a second this reads ONE of the two values (alternating Z / C, to keep each
 * pass short) and compares it with what the loaded init sets. Two consecutive
 * mismatches on an axis -> "RELOAD <init> INIT!" on the screen label (Var 170)
 * and UD_TP_BAD = 1 for the pendant LCD; when they match again the normal label
 * comes back. Skipped while no init is loaded or one is loading (var 58 <= 0),
 * so an init's own TP block can't trip it.
 *
 * KEEP IN SYNC with the SetTPParameter(PT_COUNTS_PER_INCH, ...) values in
 * kflop-init\JPB - Standard.c / Knee Z.c / HSS.c. */
#define TP_CHECK_SEC   1.0
#define TP_CPI_KNEE    427000.0
#define TP_CPI_QUILL   180000.0

/* GetTPParameter, but heartbeat-friendly like DoPC_KeepAlive: ticks UD_HEARTBEAT
 * while KMotionCNC answers and gives up after 1 s. Returns 0 on success. */
int GetTPParameter_KeepAlive(int type, int axis, double *value)
{
    int result;

    if (PC_Busy()) return -998;
    persist.UserData[PC_COMM_PERSIST + 3] = TMP;       /* reply lands at TMP (doubles) */
    persist.UserData[PC_COMM_PERSIST + 2] = axis;
    result = PC_Issue(PC_COMM_GET_TP_PARAM, type, 1.0);
    if (result != 0) return result;
    *value = GetUserDataDouble(TMP);
    return 0;
}

void CheckInitScale(void)
{
    static double next = 0.0;
    static int    turn = 0, badZ = 0, badC = 0, badT = 0, shown = 0;
    int    ident, bad;
    double want, got, t;
    char   s[40];
    char  *name;

    t = Time_sec();
    if (t < next) return;
    next = t + TP_CHECK_SEC;

    SetUserDataDouble(UD_THREADS, (double)ThreadActive);

    ident = (int)GetUserDataDouble(UD_INIT_ID);
    if (ident < 1 || ident > 3) return;                /* none loaded / one is loading */

    /* The init must still be RUNNING: KMotionCNC's STOP button kills user threads
     * 2-7, taking the init (limit watch, backup E-stop, drive enable, spindle
     * interlocks) with it, while its identity stays published. Needs no PC_COMM. */
    badT = (ThreadActive & (1 << INIT_THREAD)) ? 0 : badT + 1;

    if (turn == 0) want = (ident == 1) ? TP_CPI_QUILL : TP_CPI_KNEE;   /* Z */
    else           want = (ident == 1) ? TP_CPI_KNEE  : TP_CPI_QUILL;  /* C */

    if (GetTPParameter_KeepAlive(PT_COUNTS_PER_INCH, turn == 0 ? AXIS_Z : AXIS_C, &got) == 0)
    {
        bad = (got < want - 0.5 || got > want + 0.5);
        if (turn == 0) badZ = bad ? badZ + 1 : 0;
        else           badC = bad ? badC + 1 : 0;
    }
    turn ^= 1;

    name = (ident == 1) ? "STANDARD" : (ident == 2) ? "KNEE Z" : "HSS";
    if (badZ >= 2 || badC >= 2 || badT >= 2)
    {
        /* rewritten every check: KMotionCNC re-seeds the label when it reloads its screen */
        sprintf(s, "RELOAD %s INIT!", name);
        DROLabel(1100, 170, s);
        shown = 1;
        SetUserDataDouble(UD_TP_BAD, 1.0);
    }
    else if (badZ == 0 && badC == 0 && badT == 0)
    {
        if (shown)
        {
            sprintf(s, "%s init LOADED", name);
            DROLabel(1100, 170, s);
            shown = 0;
        }
        SetUserDataDouble(UD_TP_BAD, 0.0);
    }
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
            /* ...except that since PC_Issue gives up waiting (2026-09-28), a request
             * that timed out is still posted in the mailbox until KMotionCNC answers
             * it -- answers that take 0.5-0.9 s do happen. Acking then let that late
             * answer (KMotionCNC writes 0 to cell 100) land on top of the requester's
             * own command: 2026-10-01 the init gate's MsgBox was swallowed (no dialog,
             * "init cancelled"). So let the mailbox be answered first (heartbeat
             * alive; after 5 s ack anyway -- the requester has its own fail-safe). */
            double t_free = Time_sec() + 5.0;
            while (persist.UserData[PC_COMM_PERSIST] > 0 && Time_sec() < t_free)
            {
                WaitNextTimeSlice();
                beat += 1.0;
                SetUserDataDouble(UD_HEARTBEAT, beat);
            }
            PC_Answered();
            SetUserDataDouble(TP_HOLD_ACK, 1.0);
            while ((int)GetUserDataDouble(TP_HOLD_REQ))
            {
                WaitNextTimeSlice();
                beat += 1.0;               /* keep heartbeat alive during the hold */
                SetUserDataDouble(UD_HEARTBEAT, beat);
            }
            SetUserDataDouble(TP_HOLD_ACK, 0.0);
        }

        /* Mechanical E-stop relay (Tom Kerekes 2026-07-27). The dedicated E-stop
         * watchdog (EStopWatch.c, Thread 5) raises UD_ESTOP_REQ while the hardware
         * E-stop chain (bit 143) is open. This service is the single PC_COMM owner,
         * so IT -- not the watchdog -- issues the DoPC, which avoids a mailbox race.
         * Edge-relay: fire the interpreter Halt once per assertion, via the hang-safe
         * wrapper (raw DoPC wedges once KMotionCNC enters its own E-stop state). The
         * watchdog already stopped the trajectory (StopCoordinatedMotion) and dropped
         * the drives. */
        {
            static int prevEstopReq = 0;
            int estopReq = (int)GetUserDataDouble(UD_ESTOP_REQ);
            if (estopReq && !prevEstopReq)
            {
                /* PC_COMM_HALT, not PC_COMM_ESTOP. Jim's own bench note: PC_COMM_ESTOP
                 * locks up KMotionCNC during a running job -- UI freezes, close + init
                 * reload to recover. HALT is the safe path the STOP button uses, and it
                 * halts the interpreter so it stops feeding motion. Pairs with the
                 * watchdog's StopCoordinatedMotion(). (Tom's FORUM door-switch pattern
                 * is StopCoordinatedMotion + HALT; his email said ESTOP -- HALT is what
                 * actually works here.) */
                DoPC_KeepAlive(PC_COMM_HALT);
            }
            prevEstopReq = estopReq;
        }

        /* 1 + 2. publish live work DROs and active fixture.
         *        (GetDROs / GetFixtureIndex scribble on TMP..TMP+5 = 30..35,
         *         which is clear of everything below.) */
        /*        Non-blocking (PC_Issue): while KMotionCNC has a dialog open these
         *        simply skip -- the last values stay published -- instead of
         *        freezing this thread and stalling the heartbeat. */
        if (GetDROs_NB(dro) == 0)
            for (i = 0; i < 6; i++)
                SetUserDataDouble(UD_DRO + i, dro[i]);

        if (GetFixtureIndex_NB(&FixtureIndex) == 0)
            SetUserDataDouble(UD_FIXTURE, (double)FixtureIndex);

        /* 2b. publish the live enable state of the ACTIVE spindle for the LCD
         *     prompt. Cheap local read of the commanded output bit; reflects
         *     whatever last set it (KMotionCNC M3/M5 or our own CMD_SPINDLE).
         *     Reading the ACTIVE spindle's bit is what makes the pendant ask the
         *     RIGHT question (START? vs STOP?) under every init -- previously it
         *     always watched 156, so with the big spindle running it reported
         *     'off' and offered START?. */
        SetUserDataDouble(UD_SPINDLE, (double)ReadBit(ActiveSpindleBit()));

        /* 2b2. RELOAD INIT check: KMotionCNC's Z/C scale vs the loaded init
         *      (once a second, one axis at a time; see CheckInitScale). */
        CheckInitScale();

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
            if      (cmd == CMD_EXECUTE) res = PC_Issue(PC_COMM_EXECUTE, 0, PC_CMD_TOUT);
            else if (cmd == CMD_ESTOP)   res = DoPC_KeepAlive(PC_COMM_ESTOP);
            else if (cmd == CMD_HALT)    res = DoPC_KeepAlive(PC_COMM_HALT);   /* priority: posts regardless */
            else if (cmd == CMD_MCODE)   res = PC_Issue(PC_COMM_MCODE, (int)arg, PC_CMD_TOUT);
            else if (cmd == CMD_FRO_INC) res = PC_IssueFloat(PC_COMM_SET_FRO_INC, (float)arg, PC_CMD_TOUT);
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
                res = PC_IssueFloat(PC_COMM_SET_SSO, (float)arg, PC_CMD_TOUT);
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
                    res = PC_IssueFloat(PC_COMM_SET_X + axis, 0.0F, PC_CMD_TOUT);
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

        /* 5. heartbeat + pacing (GetDROs_NB waits on the ~10 Hz channel) */
        beat += 1.0;
        SetUserDataDouble(UD_HEARTBEAT, beat);
        Delay_sec(0.02);
    }
}
