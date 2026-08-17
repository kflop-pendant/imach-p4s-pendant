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
 * HOW IT STOPS (Tom Kerekes, 2026-08 -- the supported, clean way):
 *   PRIMARY -- DISABLE THE AXES. While a job runs, KMotionCNC continuously
 *   polls the board and self-aborts the instant any axis is disabled, reporting
 *   "Axis Disabled" (exactly what the screen Emergency Stop does). Motion output
 *   dies immediately, KMotionCNC aborts itself cleanly, there is nothing to
 *   Cancel, no PC_COMM traffic is involved, and it works with any number of
 *   clients connected. So on E-stop we DisableAxis() the enabled axes, drop the
 *   drive-enable relay (bit 155), and turn both spindles off.
 *
 *   NOT StopCoordinatedMotion() -- despite the name it is a FEEDHOLD (a
 *   resumable pause). Feedholding the buffer mid-job left KMotionCNC monitoring
 *   motion that stopped without its knowledge -> "Unexpected Coordinated Motion
 *   Buffer Underflow" and a stuck feed-hold you had to Cancel out of. Removed.
 *
 *   BELT-AND-SUSPENDERS -- UD_ESTOP_REQ (var 59) still goes high while held, and
 *   PendantService.c (Thread 7, the single PC_COMM owner) edge-relays it to a
 *   KMotionCNC Halt. Optional now that disable-axes is the primary abort, but
 *   harmless (KMotionCNC-managed, so no underflow) and kept as a second path.
 *
 * The machine inits keep their own E-stop block (DisableAxis + the same flag) as
 * a redundant backup for when they are alive; this watcher is the primary and
 * the one that survives a running job.
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
            /* PRIMARY abort: disable the axes. KMotionCNC's poll sees this and
             * self-aborts the running job ("Axis Disabled") -- clean, no Cancel. */
            for (i = 0; i < 6; i++)
                if (chan[i].Enable) DisableAxis(i);

            ClearBit(DRIVE_EN_BIT);             /* drop the stepper drive-enable relay */

            /* Both spindles OFF (matches the init's power-on safe state). */
            ClearBit(150); ClearBit(151); DAC(7, 0);   /* big OEM spindle */
            ClearBit(156); DAC(5, 0);                  /* high-speed spindle */

            SET_UD(UD_ESTOP_REQ, 1.0);          /* belt-and-suspenders -> PendantService -> Halt */
        }
        else
        {
            SET_UD(UD_ESTOP_REQ, 0.0);
        }

        WaitNextTimeSlice();
    }
}
