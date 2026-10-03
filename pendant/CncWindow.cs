/*
 * CncWindow.cs -- read KMotionCNC's "Simulate" checkbox from its window.
 *
 * With Simulate ticked, KMotionCNC only simulates: its DROs turn white and stop
 * following the machine and G-code / MDI don't move it -- but the pendant talks to
 * the KFLOP directly and keeps jogging the real machine (deliberately: its E-stop
 * must keep working). KMotionCNC doesn't tell the KFLOP about Simulate, so the
 * bridge reads the checkbox itself and the LCD flags "CNC SIM".
 *
 * Read-only: finds the control by its dialog id (IDC_Simulate) among KMotionCNC's
 * windows and sends BM_GETCHECK -- never clicks or changes anything. A custom
 * screen that gives the Simulate box button images makes it owner-drawn, which
 * Windows keeps no check state for; then (or if the control can't be found) this
 * simply reports false.
 */
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace iMachKflop
{
    static class CncWindow
    {
        // KMotionCNC resource.h: #define IDC_Simulate 2228 (KMotion 5.4.x - 5.5.x)
        const int IdcSimulate = 2228;
        const uint BM_GETCHECK = 0x00F0;
        const int BST_CHECKED = 1;

        delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr lParam);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] static extern int GetDlgCtrlID(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
                                                                          uint flags, uint timeoutMs, out IntPtr result);
        const uint SMTO_ABORTIFHUNG = 0x0002;

        static IntPtr _box = IntPtr.Zero;   // cached Simulate checkbox
        static int _boxPid;

        // True when KMotionCNC (or our KMotionCNC_dev build) is running with Simulate ticked.
        public static bool SimulateChecked()
        {
            try
            {
                int pid = CncPid();
                if (pid == 0) { _box = IntPtr.Zero; return false; }
                if (_box == IntPtr.Zero || pid != _boxPid || !IsWindow(_box))
                {
                    _box = FindSimulateBox((uint)pid);
                    _boxPid = pid;
                }
                if (_box == IntPtr.Zero) return false;

                // a hung KMotionCNC must not stall the bridge loop
                IntPtr r;
                if (SendMessageTimeout(_box, BM_GETCHECK, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 100, out r) == IntPtr.Zero)
                    return false;
                return r.ToInt64() == BST_CHECKED;
            }
            catch { return false; }
        }

        static int CncPid()
        {
            int pid = 0;
            foreach (string name in Tune.CncProcessNames)
                foreach (var p in Process.GetProcessesByName(name))
                {
                    if (pid == 0 || p.Id < pid) pid = p.Id;
                    p.Dispose();
                }
            return pid;
        }

        static IntPtr FindSimulateBox(uint pid)
        {
            IntPtr found = IntPtr.Zero;
            EnumProc child = (h, l) =>
            {
                if (GetDlgCtrlID(h) == IdcSimulate) { found = h; return false; }
                return true;
            };
            EnumWindows((h, l) =>
            {
                uint p;
                GetWindowThreadProcessId(h, out p);
                if (p == pid) EnumChildWindows(h, child, IntPtr.Zero);   // recursive over all descendants
                return found == IntPtr.Zero;
            }, IntPtr.Zero);
            return found;
        }
    }
}
