/* InitGate.c ---------------------------------------------------------------
 *
 * Confirmation gate for the screen init buttons (Standard / Knee Z / PCB, and
 * their F9 / F10 hotkeys). Loading an init restarts the drives and zeroes all
 * five DROs, so an accidental click on a set-up machine throws away the work
 * zero. The buttons run THIS program instead of the init; it decides whether
 * to ask first, then has KMotionCNC run the real init.
 *
 * KMotionCNC config (Tool Setup > M-codes / user buttons):
 *   Buttons 0/1/2 (action slots 11/12/13): Exec Prog, Thread 6, Var 160,
 *       this file. KMotionCNC writes the slot number (11/12/13) into persist
 *       int 160 before starting it -- that is how the gate knows which button.
 *   M110/M111/M112 (action slots 31/32/33): Exec Prog, Thread 4, the three
 *       inits. The gate invokes them with PC_COMM_MCODE 110/111/112 -- the
 *       M-CODE NUMBER, not the slot: InvokeAction() maps >=100 to M100+ and
 *       24..99 to the SPECIAL actions (31 would hit an empty special slot).
 *
 * Decision, from the init state the inits publish at var 58 (double-index:
 * +1/+2/+3 = that init loaded, -1/-2/-3 = that init loading, 0 = none):
 *   job running (JOB_ACTIVE)  -> refuse, no prompt (would kill the init mid-job)
 *   0  no init since power-up -> load immediately, no prompt
 *   >0 an init is loaded      -> Yes/No, default No
 *   <0 an init is mid-load    -> sharper Yes/No, default No (lets a stuck load be replaced)
 *
 * PC_COMM: the MsgBox holds the single PC_COMM mailbox until it is answered,
 * and PC_COMM_MCODE uses it too, so the whole exchange runs inside the same
 * TP_HOLD handshake the inits use -- PendantService pauses (heartbeat kept
 * alive) and cannot race the mailbox. The pendant is therefore frozen while
 * the dialog is open. The hardware E-stop is unaffected (EStopWatch, Thread 5,
 * drops the drives directly), and the dialog only opens when no job runs.
 *
 * THREAD: 6, shared with JPB_Machine_Toggle.c, which is run-once (sets a flag
 * and exits), so nothing persistent is evicted. While the MsgBox is up the
 * KMotionCNC window is modal, so the toggle cannot be launched onto 6 and kill
 * this gate mid-dialog. NEVER bind this to T4 (init), T5 (E-stop), T7 (service).
 * ---------------------------------------------------------------------- */

#include "KMotionDef.h"
#define TMP 10
#include "../shared/KflopToKMotionCNCFunctions.c"

#define GATE_VAR       160  /* persist int: KMotionCNC writes the button's action slot (11/12/13) */
#define UD_INIT_ID     58   /* double-index: init state (see above) */
#define TP_HOLD_REQ    56   /* double-index: ask PendantService to pause PC_COMM */
#define TP_HOLD_ACK    57   /* double-index: PendantService paused */
#define INIT_MCODE_BASE 109 /* PC_COMM_MCODE takes the M number: 109 + init id -> M110 Std / M111 Knee Z / M112 PCB */

#ifndef MB_DEFBUTTON2
#define MB_DEFBUTTON2  0x00000100
#endif

char *InitName(int id)
{
	if (id == 1) return "STANDARD";
	if (id == 2) return "KNEE Z";
	if (id == 3) return "PCB";
	return "UNKNOWN";
}

int main()
{
	int sel, state, answer, rc;
	double t0;
	char msg[200];	/* KMotionCNC reads at most 50 words (200 bytes) of MsgBox text */

	sel = persist.UserData[GATE_VAR] - 10;	/* 11/12/13 -> 1/2/3 */
	persist.UserData[GATE_VAR] = 0;
	if (sel < 1 || sel > 3)
	{
		printf("InitGate: bad button slot %d -- nothing loaded\n", sel + 10);
		return 0;
	}

	if (JOB_ACTIVE)
	{
		printf("InitGate: job running -- %s init NOT loaded\n", InitName(sel));
		return 0;
	}

	state = (int)GetUserDataDouble(UD_INIT_ID);	/* exact small integers, no rounding needed */

	/* Pause PendantService's PC_COMM traffic (2 s fail-safe if it isn't running). */
	SetUserDataDouble(TP_HOLD_REQ, 1.0);
	t0 = Time_sec();
	while ((int)GetUserDataDouble(TP_HOLD_ACK) != 1)
	{
		WaitNextTimeSlice();
		if (Time_sec() - t0 > 2.0) break;
	}

	if (state != 0)
	{
		if (state > 0)
		{
			sprintf(msg, "Load the %s init?\n\n%s is loaded. Loading restarts the drives "
			             "and zeroes all DROs (X Y Z A C) - any work zero will be lost.",
			        InitName(sel), InitName(state));
			answer = MsgBox(msg, MB_YESNO | MB_ICONQUESTION | MB_DEFBUTTON2 | MB_TOPMOST);
		}
		else
		{
			sprintf(msg, "%s init is still loading or was interrupted.\n\n"
			             "Load %s anyway? Interrupting a load can leave Z/C swapped. "
			             "Wait for 'init LOADED' unless it is stuck.",
			        InitName(-state), InitName(sel));
			answer = MsgBox(msg, MB_YESNO | MB_ICONEXCLAMATION | MB_DEFBUTTON2 | MB_TOPMOST);
		}

		if (answer != IDYES)
		{
			SetUserDataDouble(TP_HOLD_REQ, 0.0);
			printf("InitGate: %s init cancelled\n", InitName(sel));
			return 0;
		}
	}

	/* Run the real init (Exec Prog on Thread 4). KMotionCNC refuses (-1) if a job
	   started meanwhile. The new init only raises TP_HOLD_REQ again ~6 s later, in
	   its TP block, so releasing the hold here cannot collide with it. */
	rc = DoPCInt(PC_COMM_MCODE, INIT_MCODE_BASE + sel);
	SetUserDataDouble(TP_HOLD_REQ, 0.0);
	printf("InitGate: %s init %s (rc=%d)\n", InitName(sel), rc == 0 ? "started" : "REFUSED", rc);
	return 0;
}
