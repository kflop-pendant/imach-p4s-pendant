/* InitPrompt.c --------------------------------------------------------------
 *
 * Blinks the KMotionCNC screen's init readout (DROLabel Var 170) with
 * "CHOOSE init ->" while no init has been loaded since KFLOP power-up, so it
 * is obvious an init must be chosen before anything else will work. Exits the
 * moment any init starts.
 *
 * LAUNCHED BY THE PENDANT BRIDGE (Program.cs), not by KMotionCNC: once per
 * KFLOP power-up, when the board answers, KMotionCNC is running and no init is
 * loaded. The bridge only tries once the board responds, so -- unlike a
 * KMotionCNC "Program Start" action -- there is no "Unable to determine
 * Controller Board Type" popup when KMotionCNC opens before the KFLOP is on.
 * Deployed next to iMachKflop.exe by the build (like EStopWatch.c).
 *
 * "No init loaded" = UserData double-index 58 == 0. Each init writes -id there
 * as the first thing it does and +id once loaded; KFLOP clears it at power-up.
 * The state is re-checked immediately before every write, so once an init has
 * written its "LOADING..." label this program can no longer blank it.
 *
 * DROLabel is a plain gather-buffer/persist write (no PC_COMM), so it can't
 * race PendantService. Gather offset 1100 = the one every Var 170 write uses.
 * SELF-CONTAINED (KMotionDef.h only, like EStopWatch.c) so it compiles from
 * wherever it is deployed.
 *
 * THREAD 3 (bridge KflopLink.PromptThread). NEVER T4 (init), T5 (E-stop watch),
 * T6 (init gate / lock toggle) or T7 (PendantService). T3 is otherwise only
 * used by M102, which can't run before an init exists -- and by then this
 * program has exited.
 * ---------------------------------------------------------------------- */

#include "KMotionDef.h"

#define UD_INIT_ID  58     /* double-index: 0 none, -id loading, +id loaded */
#define LABEL_VAR   170    /* screen readout (DROLabel persist var) */
#define GATH_OFFSET 1100   /* gather-buffer word offset shared by all Var 170 writes */
#define ON_SEC      1.4    /* "CHOOSE init ->" visible  } one blink every 2 s */
#define OFF_SEC     0.6    /* blank                     } */

/* KFLOP stores a 64-bit double in a PAIR of UserData ints (index 2n, 2n+1) */
double GetUD(int i) { return *(double *)&persist.UserData[i * 2]; }

int NoInit(void) { return (int)GetUD(UD_INIT_ID) == 0; }

/* Same as KflopToKMotionCNCFunctions.c DROLabel(): copy the string into the
   gather buffer (backwards, so the terminator lands first), then point the
   persist var at it; KMotionCNC uploads and displays it. */
void Label(char *s)
{
	char *p = (char *)gather_buffer + GATH_OFFSET * sizeof(int);
	int i, n;
	for (n = 0; n < 256; n++) if (s[n] == 0) break;
	for (i = n; i >= 0; i--) p[i] = s[i];
	persist.UserData[LABEL_VAR] = GATH_OFFSET;
}

/* wait, but give up early (return 0) as soon as an init starts */
int WaitWhileNoInit(double sec)
{
	double t0 = Time_sec();
	while (Time_sec() - t0 < sec)
	{
		if (!NoInit()) return 0;
		WaitNextTimeSlice();
	}
	return 1;
}

int main()
{
	for (;;)
	{
		if (!NoInit()) return 0;
		Label("CHOOSE init ->");		/* arrow points at the init buttons */
		if (!WaitWhileNoInit(ON_SEC)) return 0;

		if (!NoInit()) return 0;
		Label(" ");				/* a space, not "", so it always reads as a real label */
		if (!WaitWhileNoInit(OFF_SEC)) return 0;
	}
}
