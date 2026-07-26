#include "KMotionDef.h"

// Sometimes when mill is first powered on, one of the axis - usually the knee or quill, 'shutters' and
// causes an alarm/fault to occur within the motor driver.  This requires pressing the drive reset 
// switch, then reloading the KFLOP config file.  This program is an attempt to prevent this from
// ever happening by disabeling the drives until the config file loads, during which a drive reset occurs.
// It is stored in thread #1 and set to run at startup.

	
	int main()
{
	ClearBit(155);	// Deactivates Relay 4 to Disable Stepper Drives until Config file is loaded
	
    return 0;
}
	

