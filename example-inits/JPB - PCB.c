#include "KMotionDef.h"
#define TMP 10
#include "../shared/KflopToKMotionCNCFunctions.c"

// Defines KFLOP channels 0-4 as axes X-C
// Disables and Zeros all axes
// Defines axis parameters
// Enables all axes and sets positions to Zero
// Sets them as an XYZAC coordinate system for GCode
// Watchenable loop turns servos ON/OFF when axes are ENABLED/DISABLED
// or when the Control Program (KMotionCNC) is loaded/ended
// Each axis is disabled when a limit switch is tripped OR when E-stop is pressed.
// To reactivate: for a limit, first move the axis clear of the switch by hand;
// then in either case re-enable via the pendant Control Lock or reload this init.
// Axes staying disabled after E-stop is released is intended, not a fault.
//
// MaxFollowingError is the primary crash / broken-encoder detector.
// Set with margin above the worst following error observed at full speed
// (~1800 counts on X/Y during rapid acceleration, July 2026).

// ** Parameters of Ch2 and Ch3 are flipped from standard startup program
// ** to allow the knee to function as the Z when following unedited G code


void ServiceWatchdogStatus(void);
void WatchdogTripped(void);
void WatchdogOK(void);
void WatchLimits(void);
void UpdateWatchdogLED(void);

#define X 0
#define Y 1
#define Z 2		// Knee
#define C 3		// Quill
#define A 4		// Rotary

#define WATCHDOG_DELAY 2.0 	// Script to disable drives if/when Control Program is closed

/* PC_COMM handshake cells — MUST match PendantService.c (double-idx 56/57).
 * Used by the TP block below to serialize with PendantService's polling.
 * See the TP block comment for Tom Kerekes' 2026-07-24 diagnosis. */
#define TP_HOLD_REQ    56
#define TP_HOLD_ACK    57

int ControlProgramActive = 1;

// TP verification flag — set by the TP block in main() based on whether
// all SetTPParameter calls returned rc=0 AND the delayed re-read matched
// the expected values. Read by WatchdogOK to decide whether to repaint
// "init LOADED" or "TP FAIL!!" on KMotionCNC reconnect (Tom 2026-07-24).
int TP_verified = 0;

// Non-blocking watchdog-LED state (Tom 2026-07-24 review — the previous
// Delay_sec(1) x 2 calls in Watchdog{OK,Tripped} blocked the forever loop's
// E-stop watching and limit monitoring for ~2s on every KMotionCNC connect
// or disconnect event). Set by WatchdogOK/Tripped, advanced by
// UpdateWatchdogLED() called once per forever-loop pass.
//   0 = idle, 1 = OK-blink pending (bit 47), 2 = Tripped-blink pending (bit 46)
int    watchdog_LED_state = 0;
double watchdog_LED_start = 0.0;

int XLimit;
int YLimit;
int ZLimit;
int CLimit;
int EStop;

int main() 
{
	int i;

	// (SetBit(154) previously here — disabled drives via the second (NC) drive-
	// enable relay during the KFLOP reset pulse. Removed 2026-07-24 per Jim:
	// the cold-start ch2 shuttering this was meant to guard against turned out
	// to have been separately fixed by the pendant bridge's startup sequence.
	// Bit 155 (primary relay) already keeps drives disabled during the reset
	// pulse anyway, since not all axes are enabled yet. If the shuttering
	// returns, restore SetBit(154) here and ClearBit(154) below.)

	Delay_sec(1);
	
	SetBit(158); 	// Activates RESET switch on KFLOP to trigger reset for all Stepper Drivers
	SetBit(46);		// Lights KFLOP onboard LED to verify Stepper Driver reset button press
	
	Delay_sec(1.0);
	
	ClearBit(158); 	// Reset complete
	ClearBit(46); 	// Turns off onboard LED to verify process complete

	// (ClearBit(154) removed 2026-07-24 — see comment before the reset pulse above.)
	Delay_sec(4.0);	// Allow drives to complete internal initialization
	
FPGA(STEP_PULSE_LENGTH_ADD)=32 + 0x40 + 0x80;	

	ch0->InputMode=ENCODER_MODE;
	ch0->OutputMode=CL_STEP_DIR_MODE;
	ch0->Vel=305340;
	ch0->Accel=3053400;
	ch0->Jerk=30534000;
	ch0->P=0;
	ch0->I=0.002;
	ch0->D=0;
	ch0->FFAccel=0;
	ch0->FFVel=0;
	ch0->MaxI=1e+09;
	ch0->MaxErr=1e+09;
	ch0->MaxOutput=500000;
	ch0->DeadBandGain=0;
	ch0->DeadBandRange=2;
	ch0->InputChan0=0;
	ch0->InputChan1=0;
	ch0->OutputChan0=56;
	ch0->OutputChan1=0;
	ch0->MasterAxis=-1;
	ch0->LimitSwitchOptions=0x120;
	ch0->LimitSwitchNegBit=136;
	ch0->LimitSwitchPosBit=136;
	ch0->SoftLimitPos=1e+09;
	ch0->SoftLimitNeg=-1e+09;
	ch0->InputGain0=-46.01;
	ch0->InputGain1=1;
	ch0->InputOffset0=0;
	ch0->InputOffset1=0;
	ch0->OutputGain=-1;
	ch0->OutputOffset=0;
	ch0->SlaveGain=1;
	ch0->BacklashMode=BACKLASH_OFF;
	ch0->BacklashAmount=0;
	ch0->BacklashRate=0;
	ch0->invDistPerCycle=1;
	ch0->Lead=0;
	ch0->MaxFollowingError=6000;
	ch0->StepperAmplitude=20;

	ch0->iir[0].B0=1;
	ch0->iir[0].B1=0;
	ch0->iir[0].B2=0;
	ch0->iir[0].A1=0;
	ch0->iir[0].A2=0;

	ch0->iir[1].B0=1;
	ch0->iir[1].B1=0;
	ch0->iir[1].B2=0;
	ch0->iir[1].A1=0;
	ch0->iir[1].A2=0;

	ch0->iir[2].B0=1;
	ch0->iir[2].B1=0;
	ch0->iir[2].B2=0;
	ch0->iir[2].A1=0;
	ch0->iir[2].A2=0;

	// Zero X, then enable it
	WaitNextTimeSlice(); // let the ISR settle with the new parameters
	Zero(X);
	EnableAxis(X);
	

	ch1->InputMode=ENCODER_MODE;
	ch1->OutputMode=CL_STEP_DIR_MODE;
	ch1->Vel=305340;
	ch1->Accel=3053400;
	ch1->Jerk=30534000;
	ch1->P=0;
	ch1->I=0.001;
	ch1->D=0;
	ch1->FFAccel=0;
	ch1->FFVel=0;
	ch1->MaxI=1e+09;
	ch1->MaxErr=1e+09;
	ch1->MaxOutput=500000;
	ch1->DeadBandGain=0;
	ch1->DeadBandRange=2;
	ch1->InputChan0=1;
	ch1->InputChan1=0;
	ch1->OutputChan0=57;
	ch1->OutputChan1=0;
	ch1->MasterAxis=-1;
	ch1->LimitSwitchOptions=0x120;
	ch1->LimitSwitchNegBit=137;
	ch1->LimitSwitchPosBit=137;
	ch1->SoftLimitPos=1e+09;
	ch1->SoftLimitNeg=-1e+09;
	ch1->InputGain0=46.01;
	ch1->InputGain1=1;
	ch1->InputOffset0=0;
	ch1->InputOffset1=0;
	ch1->OutputGain=-1;
	ch1->OutputOffset=0;
	ch1->SlaveGain=1;
	ch1->BacklashMode=BACKLASH_OFF;
	ch1->BacklashAmount=0;
	ch1->BacklashRate=0;
	ch1->invDistPerCycle=1;
	ch1->Lead=0;
	ch1->MaxFollowingError=6000;
	ch1->StepperAmplitude=20;

	ch1->iir[0].B0=1;
	ch1->iir[0].B1=0;
	ch1->iir[0].B2=0;
	ch1->iir[0].A1=0;
	ch1->iir[0].A2=0;

	ch1->iir[1].B0=1;
	ch1->iir[1].B1=0;
	ch1->iir[1].B2=0;
	ch1->iir[1].A1=0;
	ch1->iir[1].A2=0;

	ch1->iir[2].B0=1;
	ch1->iir[2].B1=0;
	ch1->iir[2].B2=0;
	ch1->iir[2].A1=0;
	ch1->iir[2].A2=0;

	// Zero Y, then enable it
	WaitNextTimeSlice(); // let the ISR settle with the new parameters
	Zero(Y);
	EnableAxis(Y);
	

	ch2->InputMode=ENCODER_MODE;
	ch2->OutputMode=CL_STEP_DIR_MODE;
	ch2->Vel=80000;
	ch2->Accel=100000;
	ch2->Jerk=300000;
	ch2->P=0.12;
	ch2->I=0.009;
	ch2->D=0.03;
	ch2->FFAccel=0.0006;
	ch2->FFVel=0.007;
	ch2->MaxI=120000;
	ch2->MaxErr=1e+09;
	ch2->MaxOutput=250000;
	ch2->DeadBandGain=0;
	ch2->DeadBandRange=25;
	ch2->InputChan0=3;
	ch2->InputChan1=0;
	ch2->OutputChan0=59;
	ch2->OutputChan1=0;
	ch2->MasterAxis=-1;
	ch2->LimitSwitchOptions=0x120;
	ch2->LimitSwitchNegBit=139;
	ch2->LimitSwitchPosBit=139;
	ch2->SoftLimitPos=1e+09;
	ch2->SoftLimitNeg=-1e+09;
	ch2->InputGain0=-85.4;
	ch2->InputGain1=1;
	ch2->InputOffset0=0;
	ch2->InputOffset1=0;
	ch2->OutputGain=-1;
	ch2->OutputOffset=0;
	ch2->SlaveGain=1;
	ch2->BacklashMode=BACKLASH_OFF;
	ch2->BacklashAmount=0;
	ch2->BacklashRate=0;
	ch2->invDistPerCycle=1;
	ch2->Lead=0;
	ch2->MaxFollowingError=5000;
	ch2->StepperAmplitude=24;

	ch2->iir[0].B0=1;
	ch2->iir[0].B1=0;
	ch2->iir[0].B2=0;
	ch2->iir[0].A1=0;
	ch2->iir[0].A2=0;

	ch2->iir[1].B0=1;
	ch2->iir[1].B1=0;
	ch2->iir[1].B2=0;
	ch2->iir[1].A1=0;
	ch2->iir[1].A2=0;

	ch2->iir[2].B0=1;
	ch2->iir[2].B1=0;
	ch2->iir[2].B2=0;
	ch2->iir[2].A1=0;
	ch2->iir[2].A2=0;

	// Zero Z, then enable it
	WaitNextTimeSlice(); // let the ISR settle with the new parameters
	Zero(Z);
	EnableAxis(Z);
	

	ch3->InputMode=ENCODER_MODE;
	ch3->OutputMode=CL_STEP_DIR_MODE;
	ch3->Vel=200000;
	ch3->Accel=2000000;
	ch3->Jerk=20000000;
	ch3->P=0;
	ch3->I=0.003;
	ch3->D=0;
	ch3->FFAccel=0;
	ch3->FFVel=0;
	ch3->MaxI=1e+09;
	ch3->MaxErr=1e+09;
	ch3->MaxOutput=400000;
	ch3->DeadBandGain=0;
	ch3->DeadBandRange=10;
	ch3->InputChan0=2;
	ch3->InputChan1=0;
	ch3->OutputChan0=58;
	ch3->OutputChan1=0;
	ch3->MasterAxis=-1;
	ch3->LimitSwitchOptions=0x120;
	ch3->LimitSwitchNegBit=138;
	ch3->LimitSwitchPosBit=138;
	ch3->SoftLimitPos=1e+09;
	ch3->SoftLimitNeg=-1e+09;
	ch3->InputGain0=-35.35;
	ch3->InputGain1=1;
	ch3->InputOffset0=0;
	ch3->InputOffset1=0;
	ch3->OutputGain=-1;
	ch3->OutputOffset=0;
	ch3->SlaveGain=1;
	ch3->BacklashMode=BACKLASH_OFF;
	ch3->BacklashAmount=0;
	ch3->BacklashRate=0;
	ch3->invDistPerCycle=1;
	ch3->Lead=0;
	ch3->MaxFollowingError=5000;
	ch3->StepperAmplitude=20;

	ch3->iir[0].B0=1;
	ch3->iir[0].B1=0;
	ch3->iir[0].B2=0;
	ch3->iir[0].A1=0;
	ch3->iir[0].A2=0;

	ch3->iir[1].B0=1;
	ch3->iir[1].B1=0;
	ch3->iir[1].B2=0;
	ch3->iir[1].A1=0;
	ch3->iir[1].A2=0;

	ch3->iir[2].B0=1;
	ch3->iir[2].B1=0;
	ch3->iir[2].B2=0;
	ch3->iir[2].A1=0;
	ch3->iir[2].A2=0;

	// Zero C, then enable it
	WaitNextTimeSlice(); // let the ISR settle with the new parameters
	Zero(C);
	EnableAxis(C);
	
	
	ch4->InputMode=NO_INPUT_MODE;
	ch4->OutputMode=STEP_DIR_MODE;
	ch4->Vel=40000;
	ch4->Accel=400000;
	ch4->Jerk=4e+06;
	ch4->P=0;
	ch4->I=0;
	ch4->D=0;
	ch4->FFAccel=0;
	ch4->FFVel=0;
	ch4->MaxI=200;
	ch4->MaxErr=200;
	ch4->MaxOutput=200;
	ch4->DeadBandGain=1;
	ch4->DeadBandRange=0;
	ch4->InputChan0=4;
	ch4->InputChan1=0;
	ch4->OutputChan0=63;
	ch4->OutputChan1=0;
	ch4->MasterAxis=-1;
	ch4->LimitSwitchOptions=0x100;
	ch4->LimitSwitchNegBit=0;
	ch4->LimitSwitchPosBit=0;
	ch4->SoftLimitPos=1e+09;
	ch4->SoftLimitNeg=-1e+09;
	ch4->InputGain0=1;
	ch4->InputGain1=1;
	ch4->InputOffset0=0;
	ch4->InputOffset1=0;
	ch4->OutputGain=1;
	ch4->OutputOffset=0;
	ch4->SlaveGain=1;
	ch4->BacklashMode=BACKLASH_OFF;
	ch4->BacklashAmount=0;
	ch4->BacklashRate=0;
	ch4->invDistPerCycle=1;
	ch4->Lead=0;
	ch4->MaxFollowingError=1000000000;
	ch4->StepperAmplitude=20;

	ch4->iir[0].B0=1;
	ch4->iir[0].B1=0;
	ch4->iir[0].B2=0;
	ch4->iir[0].A1=0;
	ch4->iir[0].A2=0;

	ch4->iir[1].B0=1;
	ch4->iir[1].B1=0;
	ch4->iir[1].B2=0;
	ch4->iir[1].A1=0;
	ch4->iir[1].A2=0;

	ch4->iir[2].B0=1;
	ch4->iir[2].B1=0;
	ch4->iir[2].B2=0;
	ch4->iir[2].A1=0;
	ch4->iir[2].A2=0;

	// Zero A, then enable it
	WaitNextTimeSlice(); // let the ISR settle with the new parameters
	Zero(A);
	EnableAxis(A);
	
	DefineCoordSystem6(0,1,2,4,-1,3);
	// Pendant config id (var 54) is now published AFTER the TP block -- see the end
	// of that block, just before the forever loop.

	
	// "PCB init LOADED" DROLabel is deferred to AFTER the TP block
	// (2026-07-23 finding: with the DROLabel here, an operator watching the
	// screen could press a new init while this one's TP block was still
	// running, killing Thread 4 mid-write and leaving Z/C swapped or with
	// one slot stuck). Deferring the message makes the screen a reliable
	// "safe to press next init" cue.

	// --- Dual-spindle selection for this configuration ---
	// Chosen by which init is loaded; read by Spindle_S / SpindleM3 / M4 / M5.
	ClearBit(150); ClearBit(151); DAC(7, 0);	// big OEM spindle OFF
	ClearBit(156); DAC(5, 0);				// high-speed spindle OFF
	persist.UserData[150] = 1;	// spindle selector: 1 = high-speed spindle

	// (Removed 2026-07-25: a Delay_sec(2.0) used to sit here to mask a "first-load"
	// anomaly where the TP block read ch->Vel/ch->Accel wrong on the first init load
	// of a session. The real cause was NOT a firmware transient -- it was the OLD
	// pendant bridge's Connect() writing ch->Vel/Accel between this init setting them
	// and the TP block reading them; the failure values 0.200/0.333 exactly matched
	// the bridge's seed writes (StepJogFeedIpm/60 and 60000/cpu). The per-command-
	// dynamics refactor removed those bridge writes, so the delay is no longer needed.
	// Verified 2026-07-25: six back-to-back init loads all read correct on load one.)

	// --- Trajectory Planner axis resolution for this configuration ---
	// Z and C swap physical axes between inits, so counts/inch AND per-axis
	// velocity / acceleration must follow. Set here so the Tool Setup screen
	// never needs hand-editing again. Vel/Accel are derived from the KFLOP
	// channel's own values (ch2->Vel etc), so retuning the channel later
	// automatically flows through to the TP values. TP Vel is in in/sec;
	// ch->Vel is in counts/sec; divide by counts/inch to convert.
	// PREREQUISITES: KMotion 5.4.2+ (for the helpers in
	// KflopToKMotionCNCFunctions.c) AND Tom's patched KMotionCNC.exe
	// (FixGetSetTPParameters_V5.4.3, 2026-07). Stock 5.4.2/5.4.3 have a
	// bug where every write lands on X Velocity regardless of Type/Axis.
	// Readback + delayed re-read of counts/inch is the canary for the whole
	// SET/GET mechanism; per Tom Kerekes 2026-07-22, a genuine comm error
	// now surfaces as a disconnect message rather than silent corruption.
	{
		int    rcZcpi, rcCcpi, rcZv, rcZa, rcCv, rcCa, rcZj, rcCj;
		double vZ,  vC, vZ2, vC2;
		double _hold_wait_start;

		// PC_COMM handshake — pause PendantService's polling until we finish
		// this block. Without this, our SetTPParameter calls (thread 4) race
		// PendantService's GetDROs/GetFixtureIndex (thread 7) at the single
		// mailbox at cells 100..107, and one side's command gets silently
		// forged — no error surfaces because the forgery happens in KFLOP
		// memory (Tom Kerekes 2026-07-24 diagnosis). 2-second timeout is
		// fail-safe: if PendantService isn't running there's no contention.
		SetUserDataDouble(TP_HOLD_REQ, 1.0);
		_hold_wait_start = Time_sec();
		while ((int)GetUserDataDouble(TP_HOLD_ACK) != 1)
		{
			WaitNextTimeSlice();
			if (Time_sec() - _hold_wait_start > 2.0) break;
		}

		rcZcpi = SetTPParameter(PT_COUNTS_PER_INCH, AXIS_Z, 427000.0);
		GetTPParameter(PT_COUNTS_PER_INCH, AXIS_Z, &vZ);
		printf("SET Z cnts/inch rc=%d  GET Z=%.0f  (want 427000)\n", rcZcpi, vZ);

		rcCcpi = SetTPParameter(PT_COUNTS_PER_INCH, AXIS_C, 180000.0);
		GetTPParameter(PT_COUNTS_PER_INCH, AXIS_C, &vC);
		printf("SET C cnts/inch rc=%d  GET C=%.0f  (want 180000)\n", rcCcpi, vC);

		// Vel/Accel — AXIS_Z is bound to ch2 (per DefineCoordSystem6 above),
		// AXIS_C to ch3. PCB: ch2 is the knee (427000 cpi), ch3 is the quill
		// (180000 cpi). Physically opposite of Standard.
		rcZv = SetTPParameter(PT_VEL,   AXIS_Z, ch2->Vel   / 427000.0);
		rcZa = SetTPParameter(PT_ACCEL, AXIS_Z, ch2->Accel / 427000.0);
		rcCv = SetTPParameter(PT_VEL,   AXIS_C, ch3->Vel   / 180000.0);
		rcCa = SetTPParameter(PT_ACCEL, AXIS_C, ch3->Accel / 180000.0);
		printf("SET Z Vel/Accel rc=%d/%d  (%.3f in/s, %.3f in/s^2)\n",
		       rcZv, rcZa, ch2->Vel/427000.0, ch2->Accel/427000.0);
		printf("SET C Vel/Accel rc=%d/%d  (%.3f in/s, %.3f in/s^2)\n",
		       rcCv, rcCa, ch3->Vel/180000.0, ch3->Accel/180000.0);

		// Jog velocities — swap per config (Tom Kerekes 2026-07-24 added
		// PT_JOG_VEL, Type=3). Hardcoded absolute in/sec values, independent
		// of Max Vel. PCB: Z=knee 0.08 in/s (4.8 IPM), C=quill 0.3 in/s
		// (18 IPM). Values chosen to match Jim's previous manual settings.
		rcZj = SetTPParameter(PT_JOG_VEL, AXIS_Z, 0.08);
		rcCj = SetTPParameter(PT_JOG_VEL, AXIS_C, 0.3);
		printf("SET Z JogVel rc=%d (0.08 in/s)  C JogVel rc=%d (0.3 in/s)\n",
		       rcZj, rcCj);

		Delay_sec(0.5);
		GetTPParameter(PT_COUNTS_PER_INCH, AXIS_Z, &vZ2);
		GetTPParameter(PT_COUNTS_PER_INCH, AXIS_C, &vC2);
		if (vZ2 != vZ || vC2 != vC)
			printf(">>> DELAYED GET DIFFERS: Z was %.0f now %.0f, C was %.0f now %.0f\n",
			       vZ, vZ2, vC, vC2);
		printf(">>> VERIFY Tool Setup | Trajectory Planner shows Z=427000 C=180000 plus per-axis Vel/Accel\n");

		// Screen "PCB init LOADED" indicator, deferred to here so it only
		// fires once TP setup is complete (see comment above). Made
		// CONDITIONAL on TP verification: if any of the six SETs returned
		// nonzero or the delayed re-read doesn't match the expected values,
		// the screen shows a distinct TP FAIL message the operator can't miss.
		{
			char _s[80];
			if (rcZcpi == 0 && rcCcpi == 0 &&
			    rcZv == 0 && rcZa == 0 && rcCv == 0 && rcCa == 0 &&
			    rcZj == 0 && rcCj == 0 &&
			    vZ2 == 427000.0 && vC2 == 180000.0)
			{
				sprintf(_s, "PCB init LOADED");
				TP_verified = 1;
			}
			else
			{
				sprintf(_s, "PCB TP FAIL!!");
				TP_verified = 0;
			}
			DROLabel(1000, 170, _s);
		}

		// Release the PC_COMM hold — PendantService can resume polling.
		SetUserDataDouble(TP_HOLD_REQ, 0.0);
	}

	// Publish the pendant config id (var 54) HERE, after the TP block, UNCONDITIONALLY
	// (on TP success AND TP-fail). It is BOTH the "init has run" signal the bridge
	// gates on AND the knee/quill selector. Publishing it only now keeps the bridge
	// from connecting -- and PendantService from touching PC_COMM -- until this init
	// is fully set up, so cold-start TP setup can't race the service. Kept OUTSIDE the
	// TP verify if/else on purpose: a TP failure must NOT leave 54 unpublished, or the
	// bridge would wait forever and the pendant would go dead.
	SetUserDataDouble(54, 2.0);	// PCB: knee on ch2, quill on ch3
	SetUserDataDouble(58, 3.0);	// pendant init identity -> LCD banner name: 3 = PCB  (var 58; NOT 56 = TP_HOLD_REQ)

	for (;;)  				 		  	// loop forever
	{
		XLimit = ReadBit(136);
		YLimit = ReadBit(137);
		ZLimit = ReadBit(139);
		CLimit = ReadBit(138);
		
		EStop = ReadBit(143);		 	// Emergency STOP for Spindle and Drives
		
		if (!XLimit || !YLimit || !ZLimit || !CLimit)
		{
			WatchLimits();
		}

		WaitNextTimeSlice();
		ServiceWatchdogStatus();
		UpdateWatchdogLED();		// Non-blocking watchdog-LED finisher (Tom 2026-07-24)
				
		if  (ControlProgramActive &&	// ControlProgramActive when loaded
   
		    (ch0->Enable) &&
		    (ch1->Enable) &&
		    (ch2->Enable) &&		    		    
		    (ch3->Enable) &&
		    (ch4->Enable))
		{
			SetBit(155);		// Enables stepper drivers IF ALL axes are enabled
		}
		else
		{
			ClearBit(155);		// Disables stepper drivers if ANY axis is disabled
		}

		if (!EStop)
		{
			// Stop KFLOP commanding motion. Deliberate -- NOT a following-error
			// side effect -- so it also covers the open-loop A axis, which can
			// never trip that protection. Portable: no machine-specific bits here.
			// The loop below clears bit 155 once the enables drop, so the drive
			// relay follows on its own. Re-enable via Control Lock or init reload.
			// NOTE: A is open loop -- its position is unreliable after any E-stop.
			for (i = 0; i < 6; i++)
				if (chan[i].Enable) DisableAxis(i);
		}

		// High-speed spindle: force OFF whenever E-stop is pressed OR KMotionCNC
		// is closed. Bit 156 = VFD enable DAC; bit 157 = spindle E-stop asserted
		// (SetBit = disabled). WatchdogTripped / WatchdogOK also touch 157 as
		// one-shot events on KMotionCNC connect/disconnect; the loop below
		// enforces the state continuously so the KMotionCNC-close case works
		// even if a G-code M3 was in effect when KMotionCNC exited.
		if (!EStop || !ControlProgramActive)
		{
			ClearBit(156);
			SetBit(157);
		}
		else
		{
			ClearBit(157);
		}
	}
		
	return 0;
}


void ServiceWatchdogStatus(void)
{
	static int Alive=FALSE;
	static int PrevStatusRequestCounter=-1;
	static double WatchdogTime=0;
	double T=Time_sec();
	
	// check if Host is requesting Status
	if (StatusRequestCounter != PrevStatusRequestCounter)
	{
		// yes, save time 
		WatchdogTime = T + WATCHDOG_DELAY;
		PrevStatusRequestCounter=StatusRequestCounter;
		if (!Alive) WatchdogOK();
		Alive=TRUE;
	}
	else
	{
		if (T > WatchdogTime) 	 // time to trigger?
		{
			if (Alive) WatchdogTripped();
			Alive=FALSE; 
		}
	}
}


void WatchdogOK(void)			// Trips when KMotionCNC is open
{
	// Repaint the init-name readout (Var 170). KMotionCNC reloads the screen
	// when it (re)opens, which resets this Style:4 DROLabel to its "CHOOSE init"
	// seed text -- while THIS init keeps running and the machine goes live again
	// as soon as ControlProgramActive is set below. Writing the label here, on
	// every reconnect, stops the screen claiming no init is loaded when one is.
	// MADE CONDITIONAL on TP_verified (Tom 2026-07-24 review): if the initial
	// TP setup failed verification, keep showing "TP FAIL!!" rather than
	// silently claiming success on reconnect.
	{
		char _s[80];
		if (TP_verified)
			sprintf(_s, "PCB init LOADED");
		else
			sprintf(_s, "PCB TP FAIL!!");
		DROLabel(1000, 170, _s);
	}

	// Kick off non-blocking LED blink pattern (Tom 2026-07-24 review — the
	// previous Delay_sec(1) x 2 calls here blocked the forever loop's E-stop
	// watching and limit monitoring for ~2s on every KMotionCNC reconnect).
	// UpdateWatchdogLED() in the forever loop finishes the blink 1 sec later.
	ClearBit(47);
	watchdog_LED_state = 1;
	watchdog_LED_start = Time_sec();
	
	ClearBit(157);				// Enable High Speed Spindle

	ControlProgramActive = 1;
}


void WatchdogTripped(void)		// Trips when KMotionCNC is closed 
{	
	// Kick off non-blocking LED blink pattern (Tom 2026-07-24 review — the
	// previous Delay_sec(1) x 2 calls here blocked the forever loop for ~2s
	// on every disconnect). UpdateWatchdogLED() in the forever loop finishes
	// the blink 1 sec later.
	ClearBit(46);
	watchdog_LED_state = 2;
	watchdog_LED_start = Time_sec();
	
	SetBit(157);				// Disable High Speed Spindle
	
	// (Previously ClearBit(154) here — leftover from the M100/M101 knee-creep
	// workflow that was retired when the actual cause was traced to a backlash/
	// FFVel setting. Removed 2026-07-24 per Jim.)
	
	ControlProgramActive = 0;	// Keeps forever loop from re-enabling drives 
								// with 'SetBit(155)'
}


void UpdateWatchdogLED(void)
{
	// Called once per forever-loop pass. Turns the transitioning LED
	// (46 for Tripped, 47 for OK) back on 1 second after WatchdogOK/
	// WatchdogTripped cleared it. Non-blocking replacement for the two
	// Delay_sec(1) calls that used to live inside those functions
	// (Tom Kerekes 2026-07-24 review).
	if (watchdog_LED_state == 0) return;
	if (Time_sec() - watchdog_LED_start < 1.0) return;

	if      (watchdog_LED_state == 1) SetBit(47);	// OK LED back on
	else if (watchdog_LED_state == 2) SetBit(46);	// Tripped LED back on
	watchdog_LED_state = 0;
}


void WatchLimits(void)
{
	if (XLimit == 0)
	{
		ClearBit(155);
		DisableAxis(X);
	}
	
	if (YLimit == 0)
	{
		ClearBit(155);
		DisableAxis(Y);
	}
	
	if (ZLimit == 0)
	{
		ClearBit(155);
		DisableAxis(Z);
	}
	
	if (CLimit == 0)
	{
		ClearBit(155);
		DisableAxis(C);
	}
	
	ClearBit(150);				// Disables VFD and stops spindle motor
	ClearBit(151);
}
