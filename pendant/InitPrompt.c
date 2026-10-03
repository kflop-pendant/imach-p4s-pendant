/* InitPrompt.c --------------------------------------------------------------
 *
 * Blinks the KMotionCNC screen's init readout (DROLabel Var 170) with
 * "CHOOSE init ->" until an init load starts, so it is obvious an init must be
 * chosen before anything else will work. Exits the moment any init starts.
 *
 * Two cases, both launched by the bridge:
 *  - no init loaded since KFLOP power-up (var 58 == 0), and
 *  - a NEW KMotionCNC session while an init is still running on the KFLOP
 *    (var 58 > 0): the freshly started KMotionCNC has lost the init's settings
 *    (Z/C scale etc. -- the pendant's RELOAD INIT case), so it must be reloaded.
 * Either way it runs until var 58 CHANGES from the value it started with: every
 * init writes -id as its first statement, even when reloading the same init.
 *
 * LAUNCHED BY THE PENDANT BRIDGE (Program.cs), not by KMotionCNC: when the
 * board answers and KMotionCNC is running, once per KFLOP power-up with no init
 * and once per new KMotionCNC session. The bridge only tries once the board responds, so -- unlike a
 * KMotionCNC "Program Start" action -- there is no "Unable to determine
 * Controller Board Type" popup when KMotionCNC opens before the KFLOP is on.
 * Deployed next to iMachKflop.exe by the build (like EStopWatch.c).
 *
 * UserData double-index 58: 0 = none since power-up, -id loading, +id loaded
 * (KFLOP clears it at power-up). The state is re-checked immediately before
 * every write, so once an init has written its "LOADING..." label this program
 * can no longer blank it.
 *
 * DROLabel is a plain gather-buffer/persist write (no PC_COMM), so it can't
 * race PendantService. Gather offset 1100 = the one every Var 170 write uses.
 * SELF-CONTAINED (KMotionDef.h only, like EStopWatch.c) so it compiles from
 * wherever it is deployed.
 *
 * THREAD 3 (bridge KflopLink.PromptThread). NEVER T4 (init), T5 (E-stop watch),
 * T6 (init gate / lock toggle) or T7 (PendantService). T3 is otherwise only
 * used by M102 (a job's logger). Normally this program has exited before any
 * job runs; if a job is started without reloading the init in a new KMotionCNC
 * session, M102 simply replaces it on T3 and the blinking stops -- harmless.
 * ---------------------------------------------------------------------- */

#include "KMotionDef.h"

#define UD_INIT_ID  58     /* double-index: 0 none, -id loading, +id loaded */
#define UD_PROMPT_BEAT 74  /* double-index: Time_sec() while blinking, 0 when not --
                             InitGate skips its "an init is loaded" confirmation for
                             the init the prompt asks for (persist 148/149) */
#define LABEL_VAR   170    /* screen readout (DROLabel persist var) */
#define GATH_OFFSET 1100   /* gather-buffer word offset shared by all Var 170 writes */
#define ON_SEC      1.4    /* "CHOOSE init ->" visible  } one blink every 2 s */
#define OFF_SEC     0.6    /* blank                     } */

/* KFLOP stores a 64-bit double in a PAIR of UserData ints (index 2n, 2n+1) */
double GetUD(int i) { return *(double *)&persist.UserData[i * 2]; }
void SetUD(int i, double v) { *(double *)&persist.UserData[i * 2] = v; }

int startState;   /* var 58 when launched */

/* still waiting: no init load has started since this program was launched */
int NoInit(void) { return (int)GetUD(UD_INIT_ID) == startState; }

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
		SetUD(UD_PROMPT_BEAT, Time_sec());	/* "the prompt is up" -- fresh every slice */
		WaitNextTimeSlice();
	}
	return 1;
}

int Prompt(void)
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

int main()
{
	startState = (int)GetUD(UD_INIT_ID);
	if (startState < 0) return 0;		/* an init is loading right now: it owns the label */
	SetUD(UD_PROMPT_BEAT, Time_sec());
	Prompt();
	SetUD(UD_PROMPT_BEAT, 0.0);		/* the prompt is down: the gate confirms as usual */
	return 0;
}
