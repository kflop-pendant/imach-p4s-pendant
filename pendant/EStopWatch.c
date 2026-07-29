/* EStopWatch.c -----------------------------------------------------------
 *
 * DEDICATED mechanical E-stop watchdog. Runs ALONE on KFLOP Thread 5 and
 * does nothing but watch the E-stop and stop the machine. This is the
 * highest-priority safety loop on the board: stopping a moving mill comes
 * before every other action.
 *
 * WHY THIS EXISTS (root cause, 2026-07-29):
 * The E-stop response used to live only inside each machine init's forever
 * loop (JPB - Standard / Knee Z / PCB). Those inits are launched on KFLOP
 * threads that KMotionCNC ALSO launches M-codes onto (Standard=T2 shared
 * with Spindle_S/M3/M4/M5; Knee Z=T3 shared with the M102 Adaptive Logger).
 * KFLOP overwrites whatever program already occupies a thread when a new one
 * is loaded there, so the FIRST spindle word / logger of a job EVICTED the
 * init -- and with it the only code watching bit 143. The init's `hb`
 * heartbeat froze at the exact console line the M-code launched, and for the
 * rest of the cut nothing read the E-stop. Bit 143 dropped correctly in the
 * KFLOP I/O screen; there was simply no longer anyone looking.
 *
 * THE FIX: this program owns the E-stop on Thread 5, which NOTHING else is
 * ever launched onto. It cannot be evicted by a job and contains no blocking
 * call, so it cannot be
 * starved. It is loaded + kept alive by the pendant bridge (KflopLink.cs
 * LaunchWatch, twin of the PendantService load on Thread 7), and then runs
 * autonomously on the KFLOP even if the PC/bridge later dies.
 *
 * TWO HALVES OF THE STOP (both required):
 *   1. StopCoordinatedMotion() -- board-side. Brings the G-code trajectory
 *      to an emergency stop ASAP. Called every pass WHILE HELD so that even
 *      if the PC interpreter feeds another segment before it is halted, the
 *      new motion is re-killed immediately. DisableAxis + drop the
 *      drive-enable relay (bit 155) make-safe the drives, matching what the
 *      init does when it is alive.
 *   2. UD_ESTOP_REQ (var 59) -- PendantService.c on Thread 7 (also never
 *      evicted, the single PC_COMM owner) edge-relays this to
 *      DoPC(PC_COMM_HALT), which aborts the KMotionCNC interpreter so it
 *      stops feeding motion. StopCoordinatedMotion alone canNOT stop the PC
 *      interpreter -- only PC_COMM_HALT does.
 *
 * The machine inits keep their own E-stop block as a redundant backup for
 * when they happen to be alive (idle, no job); this watcher is the primary
 * and is the one that survives a running job.
 *
 * Self-contained: needs only KMotionDef.h (no shared helper, no TMP/scratch).
 * ---------------------------------------------------------------------- */

#include "KMotionDef.h"

#define ESTOP_BIT     143   /* Kanalog opto input: 24V E-stop chain, LOW = pressed */
#define DRIVE_EN_BIT  155   /* stepper drive-enable relay (drops = drives disabled) */
#define UD_ESTOP_REQ   59   /* out: PendantService (T7) edge-relays 1 -> DoPC(PC_COMM_HALT) */
#define UD_WATCH_HB    67   /* out: liveness heartbeat the bridge checks at load */

/* Write a double-indexed UserData cell DIRECTLY, exactly as JPB_Machine_Toggle.c
 * does. SetUserDataDouble() lives in the shared helper (KflopToKMotionCNCFunctions.c),
 * which this self-contained watcher does NOT include -- and persist.UserData is in
 * KMotionDef.h. A KFLOP double occupies a PAIR of UserData ints: double-index N ->
 * persist.UserData[2*N] and [2*N+1]. The bridge/PendantService read these same cells
 * via GetUserDataDouble(N), so a direct write here is byte-for-byte equivalent. */
#define SET_UD(n, v)   (*(double *)&persist.UserData[(n) * 2] = (v))

main()
{
    int    i;
    double hb = 0.0;

    for (;;)
    {
        SET_UD(UD_WATCH_HB, ++hb);              /* prove to the bridge we are running */

        if (!ReadBit(ESTOP_BIT))                /* E-stop pressed (chain open) */
        {
            StopCoordinatedMotion();            /* #1 stop the G-code trajectory NOW */
            SET_UD(UD_ESTOP_REQ, 1.0);          /* #2 -> PendantService -> PC_COMM_HALT */

            ClearBit(DRIVE_EN_BIT);             /* drop the drive-enable relay */
            for (i = 0; i < 6; i++)
                if (chan[i].Enable) DisableAxis(i);
        }
        else
        {
            SET_UD(UD_ESTOP_REQ, 0.0);
        }

        WaitNextTimeSlice();
    }
}
