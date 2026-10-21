/*
 * CncWindow.cs -- read two things from KMotionCNC's window: the "Simulate"
 * checkbox and the feed rate override.
 *
 * Simulate: with it ticked, KMotionCNC only simulates: its DROs turn white and stop
 * following the machine and G-code / MDI don't move it -- but the pendant talks to
 * the KFLOP directly and keeps jogging the real machine (deliberately: its E-stop
 * must keep working). KMotionCNC doesn't tell the KFLOP about Simulate, so the
 * bridge reads the checkbox itself and the LCD flags "CNC SIM".
 *
 * Feed rate override (the pendant's F% readout, 2026-10-21): KMotionCNC splits the
 * override between the KFLOP and its own planner, so the KFLOP's value is not the
 * one on screen (above 100% it stays at 100%). The screen is the only true source.
 * Its box (IDC_FeedRateEdit) shows the FEED override only while the Feed radio
 * button is selected -- with Rapid selected it shows the rapid override -- so the
 * value is reported only when Feed is checked, otherwise "unknown".
 *
 * Spindle speed override (S%): the box IDC_SpindleRateEdit always shows it. The
 * pendant shows it and starts each wheel adjustment from it, so the pendant and
 * the screen slider stay in step.
 *
 * Read-only: finds controls by their dialog id among KMotionCNC's windows and sends
 * BM_GETCHECK / WM_GETTEXT -- never clicks or changes anything. A custom screen
 * that gives a button images makes it owner-drawn, which Windows keeps no check
 * state for; then (or if the control can't be found) this reports false / unknown.
 */
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace iMachKflop
{
    static class CncWindow
    {
        // KMotionCNC resource.h (KMotion 5.4.x - 5.5.x)
        const int IdcSimulate     = 2228;
        const int IdcFeed         = 1451;   // the Feed radio button (Rapid is 1949)
        const int IdcFeedRateEdit = 1456;   // override box: feed or rapid, per the radio
        const int IdcSpindleRateEdit = 2258; // spindle speed override box
        const uint BM_GETCHECK = 0x00F0;
        const uint WM_GETTEXT  = 0x000D;
        const int BST_CHECKED = 1;

        delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr lParam);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] static extern int GetDlgCtrlID(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
                                                                          uint flags, uint timeoutMs, out IntPtr result);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageTimeoutW")]
        static extern IntPtr SendMessageTimeoutText(IntPtr hWnd, uint msg, IntPtr wParam, StringBuilder lParam,
                                                    uint flags, uint timeoutMs, out IntPtr result);
        const uint SMTO_ABORTIFHUNG = 0x0002;
        const uint TimeoutMs = 100;          // a hung KMotionCNC must not stall the bridge loop

        static readonly Dictionary<int, IntPtr> _ctl = new Dictionary<int, IntPtr>();   // cached controls by id
        static int _ctlPid;

        // True when KMotionCNC (or our KMotionCNC_dev build) is running with Simulate ticked.
        public static bool SimulateChecked()
        {
            try
            {
                IntPtr box = Control(IdcSimulate);
                return box != IntPtr.Zero && Checked(box);
            }
            catch { return false; }
        }

        // KMotionCNC's feed rate override (1.0 = 100%), or null when it can't be known:
        // KMotionCNC not running, the Rapid radio selected (the box then shows the
        // rapid override), or the controls unreadable.
        public static double? FeedOverride()
        {
            try
            {
                IntPtr feed = Control(IdcFeed);
                if (feed == IntPtr.Zero || !Checked(feed)) return null;
                return ReadNumber(IdcFeedRateEdit);
            }
            catch { return null; }
        }

        // KMotionCNC's spindle speed override (1.0 = 100%), or null when unreadable.
        public static double? SpindleOverride()
        {
            try { return ReadNumber(IdcSpindleRateEdit); }
            catch { return null; }
        }

        // The number shown in a KMotionCNC edit box, or null.
        static double? ReadNumber(int id)
        {
            IntPtr box = Control(id);
            if (box == IntPtr.Zero) return null;

            var sb = new StringBuilder(32);
            IntPtr r;
            if (SendMessageTimeoutText(box, WM_GETTEXT, (IntPtr)sb.Capacity, sb, SMTO_ABORTIFHUNG, TimeoutMs, out r) == IntPtr.Zero)
                return null;
            double v;
            if (!double.TryParse(sb.ToString().Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return null;
            return (v >= 0.0 && v < 10.0) ? v : (double?)null;
        }

        static bool Checked(IntPtr h)
        {
            IntPtr r;
            if (SendMessageTimeout(h, BM_GETCHECK, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, TimeoutMs, out r) == IntPtr.Zero)
                return false;
            return r.ToInt64() == BST_CHECKED;
        }

        // The KMotionCNC control with this dialog id (cached per KMotionCNC process).
        static IntPtr Control(int id)
        {
            int pid = CncPid();
            if (pid == 0) { _ctl.Clear(); return IntPtr.Zero; }
            if (pid != _ctlPid) { _ctl.Clear(); _ctlPid = pid; }

            IntPtr h;
            if (_ctl.TryGetValue(id, out h) && IsWindow(h)) return h;
            h = FindControl((uint)pid, id);
            if (h != IntPtr.Zero) _ctl[id] = h; else _ctl.Remove(id);
            return h;
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

        static IntPtr FindControl(uint pid, int id)
        {
            IntPtr found = IntPtr.Zero;
            EnumProc child = (h, l) =>
            {
                if (GetDlgCtrlID(h) == id) { found = h; return false; }
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
