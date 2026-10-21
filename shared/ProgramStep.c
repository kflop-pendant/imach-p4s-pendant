/* ProgramStep.c ------------------------------------------------------------
 *
 * PRG STEP: the pendant handwheel scrolls the running G-code program forward or
 * BACKWARD along its programmed path. Included by each init and called from the
 * init's main loop once per time slice (ServiceProgramStep()). The init thread
 * never blocks on the PC, so the servo runs every slice.
 *
 * Adapted from Tom Kerekes' MPGPanelTimeServo.c (2026-10-15, "PRG.STP" mode,
 * posted on the Dynomotion forum). The handwheel there was a KFLOP
 * encoder; here the pendant bridge posts the wheel as a running total of program
 * seconds (UD_PS_WHEEL), and the step size is chosen on the pendant.
 *
 * HOW IT WORKS (Tom): coordinated motion is a buffer of time segments executed at
 * a rate set by the feed rate override. FRO 1 = program time runs in real time,
 * 0 = the program stands still, negative = it runs BACKWARDS along the buffered
 * path, as far back as the buffer still holds. This keeps a target program time;
 * each wheel click moves the target, and every slice the FRO is set in
 * proportion to the distance to it -- never faster than programmed, and never
 * more than the machine's own deceleration can still stop within -- so the
 * program glides onto the target and stops exactly on it. The machine never
 * leaves the programmed path, so there is nothing to jump back to.
 *
 * The FRO is steered with the "Temp" calls, which leave the FRO KMotionCNC set
 * (CS0_LastFRO) untouched. A real Feed Hold always wins (the Temp calls would
 * override it, so nothing is sent while one is active). Cycle Start in this mode
 * is caught a few milliseconds in and backed up to the start: nothing moves until
 * the wheel turns. Going backwards, buffered bit outputs are skipped, not undone;
 * M-codes that run KFLOP programs (spindle etc.) make KMotionCNC finish the
 * buffer first, so the program cannot be backed up past them.
 *
 * LEAVING THE MODE while a program is running: the BRIDGE engages a real Feed
 * Hold first and only then clears UD_PS_ACTIVE, so the program never continues on
 * its own -- resume it with Start+EN as after any feed hold. (Tom's original lets
 * it continue at the normal override; deliberately not done here.)
 *
 * UserData double-indices (free block 76..79, 2026-10-20):
 *   76 UD_PS_ACTIVE  in : 1 = PRG STEP selected on the pendant, 0 = not
 *   77 UD_PS_WHEEL   in : running total of handwheel program-seconds (the bridge
 *                         only ever adds to it; we act on the change)
 *   78 UD_PS_STATE   out: 0 no job, 1 job running (mode off), 2 at target,
 *                         3 moving to target, 4 feed hold, 5 at buffer edge
 *   79 UD_PS_TIME    out: program time now (seconds), for the LCD
 *
 * Screen label: persist Var 174 (DROLabel, gather offset 1150 -- clear of 1000
 * lock label, 1024 MDI text, 1100 init label) shows the mode on KMotionCNC's
 * screen (any Style-4 DRO-label button bound to Var 174): blank when off, else "Program Step Mode:"
 * READY / PARKED / MOVING / HOLD / END OF BUFFER (each centred on its own). KMotionCNC's own Feed
 * Hold button does NOT light while parked: PRG STEP holds the program with a
 * temporary FRO of 0, not a feed hold (which would freeze the wheel too).
 * ---------------------------------------------------------------------- */

#define UD_PS_ACTIVE  76
#define UD_PS_WHEEL   77
#define UD_PS_STATE   78
#define UD_PS_TIME    79

#define PS_IDLE        0
#define PS_JOB         1
#define PS_AT_TARGET   2
#define PS_MOVING      3
#define PS_HOLD        4
#define PS_EDGE        5

#define PS_LABEL_VAR   174	// persist var of the screen label (IDC_But6, Style 4)
#define PS_LABEL_GATH  1150	// gather-buffer word offset for its text

#define TS_GAIN        10.0    // FRO per second of time error (approach time constant 0.1 s)
#define TS_FRO_MAX     1.0     // never faster than programmed, forward or backward
#define TS_DEADBAND    0.0002  // seconds: this close to the target commands FRO 0
#define TS_LAUNCH_RAMP 0.005   // seconds to bring FRO 1 -> 0 when a program starts in this mode
#define TS_MAX_LEAD    0.5     // the target may lead/trail the program by at most this many
                               // seconds, so a fast spin coasts at most this much after the
                               // wheel stops (Jim 2026-10-20: it ran on like a fast jog)

// Program time, read consistently: the servo interrupt moves time from CS0_t
// into CS0_TimeExecuted at every segment boundary, so re-read if that happened.
double PS_ReadProgramTime(void)
{
	double e, t;
	int i;
	for (i = 0; i < 4; i++)
	{
		e = CS0_TimeExecuted;
		t = CS0_t;
		if (e == CS0_TimeExecuted) break;
	}
	return e + t;
}

// Screen label, rewritten only when it changes. code: -1 = mode off (blank),
// otherwise the PS_* state while the mode is on.
int PS_LastLabel = -99;
void PS_Label(int code)
{
	if (code == PS_LastLabel) return;
	PS_LastLabel = code;
	if      (code < 0)             DROLabel(PS_LABEL_GATH, PS_LABEL_VAR, "");
	// Each state centred on its own (KMotionCNC centres label text). Padding them
	// to one length instead holds "Program Step Mode:" still but left-shifts the
	// short ones (tried 2026-10-21).
	else if (code == PS_AT_TARGET) DROLabel(PS_LABEL_GATH, PS_LABEL_VAR, "Program Step Mode: PARKED");
	else if (code == PS_MOVING)    DROLabel(PS_LABEL_GATH, PS_LABEL_VAR, "Program Step Mode: MOVING");
	else if (code == PS_HOLD)      DROLabel(PS_LABEL_GATH, PS_LABEL_VAR, "Program Step Mode: HOLD");
	else if (code == PS_EDGE)      DROLabel(PS_LABEL_GATH, PS_LABEL_VAR, "Program Step Mode: END OF BUFFER");
	else                           DROLabel(PS_LABEL_GATH, PS_LABEL_VAR, "Program Step Mode: READY");
}

// Clear the mode cells at init load, so a stale "active" can never catch the
// next program. The bridge re-asserts the mode if it is selected.
void InitProgramStep(void)
{
	SetUserDataDouble(UD_PS_ACTIVE, 0.0);
	SetUserDataDouble(UD_PS_STATE, (double)PS_IDLE);
	SetUserDataDouble(UD_PS_TIME, 0.0);
	PS_LastLabel = -99;
	PS_Label(-1);
}

// Publish the state for the bridge and the screen label.
void PS_SetState(int state, int active)
{
	SetUserDataDouble(UD_PS_STATE, (double)state);
	PS_Label(active ? state : -1);
}

void ServiceProgramStep(void)
{
	static int    WasActive = FALSE, WasExecuting = FALSE, LaunchCatch = FALSE;
	static int    AtEdge = FALSE;	// the wheel pushed into a buffer boundary; held until it turns away
	static double Target = 0, LastNow = 0, RampTime = 0.5, RampCheckTime = 0, LastFRO = 0, LastWheel = 0;
	double now, err, aerr, lo, hi, cap, fro, T, wheel, dwheel;
	int active, executing, state, entering, edge = FALSE;

	active    = GetUserDataDouble(UD_PS_ACTIVE) > 0.5;
	executing = (CoordSystem0 != NULL);
	now       = PS_ReadProgramTime();
	wheel     = GetUserDataDouble(UD_PS_WHEEL);
	dwheel    = wheel - LastWheel;		// the bridge only adds; act on the change
	LastWheel = wheel;
	if (dwheel >  2.0) dwheel =  2.0;	// sanity: no single pass moves the target more than 2 s
	if (dwheel < -2.0) dwheel = -2.0;
	entering  = active && !WasActive;	// the pass the mode switches on: start from "now", ignore the wheel

	SetUserDataDouble(UD_PS_TIME, now);

	if (!active)
	{
		if (WasActive)			// leaving the mode: put KMotionCNC's FRO back
		{				// (unless a real feed hold holds it -- resume restores CS0_LastFRO)
			if (CS0_StoppingState == 0)
				SetFROTemp(CS0_DoingRapid ? CS0_LastRapidFRO : CS0_LastFRO);
			WasActive = FALSE;
			LaunchCatch = FALSE;
		}
		state = !JOB_ACTIVE && !executing ? PS_IDLE : (CS0_StoppingState != 0 ? PS_HOLD : PS_JOB);
		PS_SetState(state, FALSE);
		return;
	}

	T = Time_sec();

	// nominal time to ramp FRO from 1 to 0 at the axes' max acceleration; sets both
	// the ramp the board uses and the stopping-distance limit below
	if (!WasActive || T > RampCheckTime)
	{
		RampTime = GetNominalFROChangeTime();
		if (RampTime < 0.01) RampTime = 0.01;
		RampCheckTime = T + 1.0;
	}

	// a program just started: the board set the time base to KMotionCNC's FRO at
	// launch, so stop it quickly (the axes have barely begun to accelerate) and
	// back up to where it started from
	if (WasActive && ((executing && !WasExecuting) || (now < LastNow - 0.02 && LastFRO >= 0.0)))
	{
		LaunchCatch = TRUE;
		Target = now;
	}

	// start from where the program is when entering the mode or nothing executes
	if (!WasActive || !executing)
		Target = now;

	WasActive = TRUE;
	WasExecuting = executing;
	LastNow = now;

	if (!executing)
	{
		PS_SetState(JOB_ACTIVE ? PS_AT_TARGET : PS_IDLE, TRUE);
		return;
	}

	if (CS0_StoppingState != 0)	// real feed hold: let it hold, ignore the wheel
	{
		Target = now;
		LaunchCatch = FALSE;
		PS_SetState(PS_HOLD, TRUE);
		return;
	}

	if (LaunchCatch)
	{
		SetFROwRateTemp(0.0f, TS_LAUNCH_RAMP);
		LastFRO = 0.0;
		if (CS0_TimeBase <= 1e-9) LaunchCatch = FALSE;	// at rest, the servo takes over
		PS_SetState(PS_MOVING, TRUE);
		return;
	}

	if (!entering) Target += dwheel;
	if (Target > now + TS_MAX_LEAD) Target = now + TS_MAX_LEAD;
	if (Target < now - TS_MAX_LEAD) Target = now - TS_MAX_LEAD;

	// stay within what the buffer holds: not before segments already overwritten,
	// not past what KMotionCNC has downloaded so far
	lo = ParametricIndexWrapped ? CS0_TimeLost + 0.01 : 0.0;
	hi = CS0_TimeDownloaded;
	if (Target < lo) { Target = lo; edge = TRUE; }
	if (Target > hi) { Target = hi; edge = TRUE; }
	// Sticky: one slice of "edge" is invisible on the screen and pendant (Jim
	// 2026-10-21: END OF BUFFER never showed). Hold it until the wheel moves the
	// target without hitting a boundary.
	if (edge) AtEdge = TRUE;
	else if (dwheel != 0.0 && !entering) AtEdge = FALSE;

	err = Target - now;
	aerr = err < 0.0 ? -err : err;

	if (aerr < TS_DEADBAND)
	{
		fro = 0.0;
	}
	else
	{
		fro = TS_GAIN * err;					// proportional approach
		cap = 0.9 * sqrt(2.0 * aerr / RampTime);		// can still stop at the target
		if (fro >  cap) fro =  cap;
		if (fro < -cap) fro = -cap;
		if (fro >  TS_FRO_MAX) fro =  TS_FRO_MAX;
		if (fro < -TS_FRO_MAX) fro = -TS_FRO_MAX;
	}

	// Temp: steers the rate of time (feed or rapid) without changing the FRO
	// KMotionCNC set. The board ramps to it at the nominal rate = accel limit.
	SetFROwRateTemp((float)fro, (float)RampTime);
	LastFRO = fro;

	state = fro != 0.0 ? PS_MOVING : (AtEdge ? PS_EDGE : PS_AT_TARGET);
	PS_SetState(state, TRUE);
}
