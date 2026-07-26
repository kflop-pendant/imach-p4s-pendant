/*
 * TuneConfig.cs  --  loads the user-tunable SPEED / FEEL values from an external
 * text file (pendant\pendant.conf) into Tune's static fields at STARTUP.
 *
 * WHY: so speeds/accels/step-sizes can be retuned by editing a text file and
 * restarting the bridge -- no rebuild, no redeploy (Tom Kerekes' top adoption
 * item). Only the speed/feel scalars are externalized; hardware facts (CPI), the
 * axis/mode/button enable tables, LCD strings, and AutoStart stay compiled.
 *
 * FORMAT: flat  key = value  ; '#' starts a comment (whole-line or trailing);
 * blank lines ignored; lists comma-separated. All numbers parse with
 * CultureInfo.InvariantCulture -- the SAME culture KflopLink.N() uses to format
 * the console commands, so the file's '.' decimal round-trips to the machine
 * exactly. Unit semantics are UNCHANGED: these values still feed
 * ipm/60*Cpi (counts/sec) and raw counts/sec^2 accels downstream.
 *
 * SAFETY -- fail loud, never guess a speed:
 *   Missing file, missing key, unparseable value, out-of-range value, duplicate
 *   key, or unknown key => ConfigException. Program.Main shows CONFIG/ERR on the
 *   LCD, prints the (full, all-at-once) detail, and REFUSES TO START. There is no
 *   silent fallback to the compiled defaults -- a machine tool must not run on a
 *   value you didn't intend (e.g. a typo silently reverting a cap you lowered).
 *
 * Ranges are SANITY bounds (finite; positive where zero/negative is nonsensical;
 * a few cross-field orderings), NOT policy limits -- the file is where the
 * operator tunes freely, and the startup echo + the hardware test are the real
 * check on a fat-fingered value. Nothing is written to Tune until the WHOLE file
 * validates (staged commit), so a later error can't leave a half-applied config.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace iMachKflop
{
    // Thrown when pendant.conf is missing, malformed, or out of range. Carries a
    // human-readable, possibly multi-line message listing EVERY problem found.
    public sealed class ConfigException : Exception
    {
        public ConfigException(string message) : base(message) { }
    }

    static class TuneConfig
    {
        public static void Load(string path)
        {
            if (!File.Exists(path))
                throw new ConfigException("config file not found:\n  " + path +
                    "\n(rebuild the bridge to deploy pendant.conf next to the exe, or copy it there by hand)");

            string[] lines;
            try { lines = File.ReadAllLines(path); }
            catch (Exception ex)
            {
                throw new ConfigException("could not read " + path + ":\n  " + ex.Message);
            }

            var errors = new List<string>();
            var kv     = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // ---- tokenize into key/value, catching structural problems ----
            for (int i = 0; i < lines.Length; i++)
            {
                string raw  = lines[i];
                int    hash = raw.IndexOf('#');
                string line = (hash >= 0 ? raw.Substring(0, hash) : raw).Trim();
                if (line.Length == 0) continue;

                int eq = line.IndexOf('=');
                if (eq <= 0)
                {
                    errors.Add("line " + (i + 1) + ": expected 'key = value', got: " + line);
                    continue;
                }
                string key = line.Substring(0, eq).Trim();
                string val = line.Substring(eq + 1).Trim();
                if (kv.ContainsKey(key)) errors.Add("line " + (i + 1) + ": duplicate key '" + key + "'");
                else                     kv[key] = val;
            }

            var p = new Fields(kv, errors);

            // ---- pull + validate every required key into staging locals ----
            // (nothing is committed to Tune until the whole file validates)
            double ipmXY    = p.Pos("IpmXY");
            double ipmKnee  = p.Pos("IpmKnee");
            double ipmQuill = p.Pos("IpmQuill");

            double dpmA     = p.Pos("DpmA");
            double dpmB     = p.Pos("DpmB");
            double jogAccA  = p.Pos("JogAccA");
            double jogAccB  = p.Pos("JogAccB");
            double stepAccA = p.Pos("StepAccA");
            double stepAccB = p.Pos("StepAccB");

            double jogAccXY    = p.Pos("JogAccXY");
            double jogAccKnee  = p.Pos("JogAccKnee");
            double jogAccQuill = p.Pos("JogAccQuill");

            double stepAccXY    = p.Pos("StepAccXY");
            double stepAccKnee  = p.Pos("StepAccKnee");
            double stepAccQuill = p.Pos("StepAccQuill");

            double[] stepSizes    = p.PosList("StepSizes", 1, 9);
            double   stepJogFeed  = p.Pos("StepJogFeedIpm");

            double fullScaleDps = p.Pos("FullScaleDPS");
            double wheelTauSec  = p.Pos("WheelTauSec");

            double csSetPoint     = p.Pos("CsSetPoint");
            double csPctPerDetent = p.Pos("CsPctPerDetent");
            double csRateFloor    = p.NonNeg("CsRateFloor");
            double csRateCeil     = p.Pos("CsRateCeil");
            int    csStepsToStart = p.IntMin("CsStepsToStart", 0);

            double halfSpeedMul = p.Pos("HalfSpeedMul");

            double gotozZClear = p.Pos("GotozZClearInch");
            double gotozIpmZ   = p.Pos("GotozIpmZ");
            double gotozIpmXY  = p.Pos("GotozIpmXY");

            double ssoStep    = p.Pos("SsoStepPerDetent");
            double ssoMin     = p.NonNeg("SsoMin");
            double ssoMax     = p.Pos("SsoMax");
            double ssoDefault = p.NonNeg("SsoDefault");

            int droDecimals = p.IntOneOf("DroDecimals", 2, 3, 4);

            // ---- cross-field sanity (only when the members parsed cleanly) ----
            if (p.Ok("HalfSpeedMul") && halfSpeedMul > 1.0)
                errors.Add("HalfSpeedMul must be in (0, 1]; got " + Inv(halfSpeedMul));
            if (p.Ok("CsRateFloor") && p.Ok("CsRateCeil") && csRateCeil <= csRateFloor)
                errors.Add("CsRateCeil (" + Inv(csRateCeil) + ") must be > CsRateFloor (" + Inv(csRateFloor) + ")");
            if (p.Ok("SsoMin") && p.Ok("SsoMax") && ssoMax <= ssoMin)
                errors.Add("SsoMax (" + Inv(ssoMax) + ") must be > SsoMin (" + Inv(ssoMin) + ")");
            if (p.Ok("SsoMin") && p.Ok("SsoMax") && p.Ok("SsoDefault") &&
                (ssoDefault < ssoMin || ssoDefault > ssoMax))
                errors.Add("SsoDefault (" + Inv(ssoDefault) + ") must be within [SsoMin, SsoMax]");

            // ---- unknown keys (typos) are errors, not silently ignored ----
            foreach (var key in kv.Keys)
                if (!p.WasUsed(key))
                    errors.Add("unknown key '" + key + "' (typo? not one of the recognized settings)");

            if (errors.Count > 0)
            {
                errors.Sort(StringComparer.OrdinalIgnoreCase);
                var sb = new StringBuilder();
                sb.Append(errors.Count).Append(" problem(s) in ").Append(path).Append(':');
                foreach (var e in errors) sb.Append("\n  - ").Append(e);
                throw new ConfigException(sb.ToString());
            }

            // ---- COMMIT: everything validated -> write to Tune, rebuild Axes ----
            Tune.IpmXY = ipmXY;  Tune.IpmKnee = ipmKnee;  Tune.IpmQuill = ipmQuill;

            Tune.DpmA = dpmA;  Tune.DpmB = dpmB;
            Tune.JogAccA = jogAccA;  Tune.JogAccB = jogAccB;
            Tune.StepAccA = stepAccA;  Tune.StepAccB = stepAccB;

            Tune.JogAccXY = jogAccXY;  Tune.JogAccKnee = jogAccKnee;  Tune.JogAccQuill = jogAccQuill;
            Tune.StepAccXY = stepAccXY;  Tune.StepAccKnee = stepAccKnee;  Tune.StepAccQuill = stepAccQuill;

            Tune.StepSizes = stepSizes;  Tune.StepJogFeedIpm = stepJogFeed;

            Tune.FullScaleDPS = fullScaleDps;  Tune.WheelTauSec = wheelTauSec;

            Tune.CsSetPoint = csSetPoint;  Tune.CsPctPerDetent = csPctPerDetent;
            Tune.CsRateFloor = csRateFloor;  Tune.CsRateCeil = csRateCeil;
            Tune.CsStepsToStart = csStepsToStart;

            Tune.HalfSpeedMul = halfSpeedMul;

            Tune.GotozZClearInch = gotozZClear;  Tune.GotozIpmZ = gotozIpmZ;  Tune.GotozIpmXY = gotozIpmXY;

            Tune.SsoStepPerDetent = ssoStep;  Tune.SsoMin = ssoMin;
            Tune.SsoMax = ssoMax;  Tune.SsoDefault = ssoDefault;

            Tune.DroDecimals = droDecimals;

            // Rebuild the compiled axis table from the values just applied (its
            // MaxRate/JogAccel/StepAccel numbers come from the scalars above).
            Tune.Axes = Tune.BuildAxes();

            Echo(path);
        }

        // Print every applied value so a `-Action Console` restart is a full audit
        // of exactly what the machine will move at (invariant '.' decimal).
        static void Echo(string path)
        {
            Console.WriteLine("Config loaded from " + path + ":");
            Console.WriteLine("  IPM caps       XY=" + Inv(Tune.IpmXY) + "  Knee=" + Inv(Tune.IpmKnee) + "  Quill=" + Inv(Tune.IpmQuill));
            Console.WriteLine("  Rotary         DpmA=" + Inv(Tune.DpmA) + "  DpmB=" + Inv(Tune.DpmB) +
                              "  JogAccA=" + Inv(Tune.JogAccA) + "  JogAccB=" + Inv(Tune.JogAccB) +
                              "  StepAccA=" + Inv(Tune.StepAccA) + "  StepAccB=" + Inv(Tune.StepAccB));
            Console.WriteLine("  Jog accel      XY=" + Inv(Tune.JogAccXY) + "  Knee=" + Inv(Tune.JogAccKnee) + "  Quill=" + Inv(Tune.JogAccQuill));
            Console.WriteLine("  Step accel     XY=" + Inv(Tune.StepAccXY) + "  Knee=" + Inv(Tune.StepAccKnee) + "  Quill=" + Inv(Tune.StepAccQuill));
            Console.WriteLine("  Step           Sizes=[" + InvList(Tune.StepSizes) + "]  FeedIpm=" + Inv(Tune.StepJogFeedIpm));
            Console.WriteLine("  Velocity (Vv)  FullScaleDPS=" + Inv(Tune.FullScaleDPS) + "  WheelTauSec=" + Inv(Tune.WheelTauSec));
            Console.WriteLine("  Continuous(Cs) SetPoint=" + Inv(Tune.CsSetPoint) + "  Pct/Detent=" + Inv(Tune.CsPctPerDetent) +
                              "  Floor=" + Inv(Tune.CsRateFloor) + "  Ceil=" + Inv(Tune.CsRateCeil) + "  StepsToStart=" + Tune.CsStepsToStart);
            Console.WriteLine("  HalfSpeedMul   " + Inv(Tune.HalfSpeedMul));
            Console.WriteLine("  GOTOZ          ClearInch=" + Inv(Tune.GotozZClearInch) + "  IpmZ=" + Inv(Tune.GotozIpmZ) + "  IpmXY=" + Inv(Tune.GotozIpmXY));
            Console.WriteLine("  Spindle (S%)   Step/Detent=" + Inv(Tune.SsoStepPerDetent) + "  Min=" + Inv(Tune.SsoMin) +
                              "  Max=" + Inv(Tune.SsoMax) + "  Default=" + Inv(Tune.SsoDefault));
            Console.WriteLine("  DroDecimals    " + Tune.DroDecimals);
        }

        static string Inv(double v)  => v.ToString(CultureInfo.InvariantCulture);
        static string InvList(double[] a)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < a.Length; i++) { if (i > 0) sb.Append(", "); sb.Append(Inv(a[i])); }
            return sb.ToString();
        }

        // Pulls typed values out of the key/value map, tracking which keys were
        // consumed (so leftovers are flagged as unknown) and which parsed cleanly
        // (so cross-field checks don't cascade off a value that never parsed).
        sealed class Fields
        {
            readonly Dictionary<string, string> _kv;
            readonly List<string>               _errors;
            readonly HashSet<string>            _used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            readonly HashSet<string>            _ok   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public Fields(Dictionary<string, string> kv, List<string> errors) { _kv = kv; _errors = errors; }

            public bool WasUsed(string key) => _used.Contains(key);
            public bool Ok(string key)      => _ok.Contains(key);

            // Get the raw string for a required key; records the key as "used".
            // Returns null (and logs a missing-key error) if absent.
            string Raw(string key)
            {
                _used.Add(key);
                if (_kv.TryGetValue(key, out string v)) return v;
                _errors.Add("missing required key '" + key + "'");
                return null;
            }

            bool TryDouble(string key, out double v)
            {
                v = 0.0;
                string s = Raw(key);
                if (s == null) return false;
                if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ||
                    double.IsNaN(v) || double.IsInfinity(v))
                {
                    _errors.Add("key '" + key + "': not a valid number: " + s);
                    return false;
                }
                return true;
            }

            public double Pos(string key)
            {
                if (!TryDouble(key, out double v)) return 0.0;
                if (v <= 0.0) { _errors.Add("key '" + key + "': must be > 0; got " + Inv(v)); return 0.0; }
                _ok.Add(key);
                return v;
            }

            public double NonNeg(string key)
            {
                if (!TryDouble(key, out double v)) return 0.0;
                if (v < 0.0) { _errors.Add("key '" + key + "': must be >= 0; got " + Inv(v)); return 0.0; }
                _ok.Add(key);
                return v;
            }

            public int IntMin(string key, int min)
            {
                string s = Raw(key);
                if (s == null) return min;
                if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                { _errors.Add("key '" + key + "': not a valid integer: " + s); return min; }
                if (v < min) { _errors.Add("key '" + key + "': must be >= " + min + "; got " + v); return min; }
                _ok.Add(key);
                return v;
            }

            public int IntOneOf(string key, params int[] allowed)
            {
                string s = Raw(key);
                if (s == null) return allowed[0];
                if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                { _errors.Add("key '" + key + "': not a valid integer: " + s); return allowed[0]; }
                foreach (int a in allowed) if (v == a) { _ok.Add(key); return v; }
                _errors.Add("key '" + key + "': must be one of [" + string.Join(", ", allowed) + "]; got " + v);
                return allowed[0];
            }

            // Comma-separated list of positive doubles; length must be [min,max].
            public double[] PosList(string key, int minCount, int maxCount)
            {
                string s = Raw(key);
                if (s == null) return new double[0];
                string[] parts = s.Split(',');
                var list = new List<double>();
                bool bad = false;
                foreach (string part in parts)
                {
                    string t = part.Trim();
                    if (t.Length == 0) continue;
                    if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ||
                        double.IsNaN(d) || double.IsInfinity(d) || d <= 0.0)
                    { _errors.Add("key '" + key + "': list entry not a positive number: " + t); bad = true; continue; }
                    list.Add(d);
                }
                if (list.Count < minCount || list.Count > maxCount)
                { _errors.Add("key '" + key + "': need " + minCount + ".." + maxCount + " entries; got " + list.Count); bad = true; }
                if (bad) return list.ToArray();
                _ok.Add(key);
                return list.ToArray();
            }
        }
    }
}
