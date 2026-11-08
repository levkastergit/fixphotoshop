using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FixPhotoshop
{
    enum Level { Info, Ok, Warn, Error }

    sealed class WinInfo
    {
        public IntPtr Hwnd;
        public uint ThreadId;
        public string Title;
        public string ClassName;
        public bool Hung;
    }

    sealed class ModuleInfo
    {
        public string Name;
        public string FilePath;
        public string Company;
        public string Version;
        public ulong Base;
        public ulong End;
    }

    sealed class Frame
    {
        public ulong Address;
        public string Module;
        public string Symbol;
        public ulong Offset;

        public override string ToString()
        {
            string m = Module ?? "???";
            if (!string.IsNullOrEmpty(Symbol))
                return string.Format("{0}!{1}+0x{2:x}", m, Symbol, Offset);
            return string.Format("{0}+0x{1:x}", m, Offset);
        }
    }

    sealed class ThreadStack
    {
        public uint ThreadId;
        public bool IsUiThread;
        public bool GotContext;
        public bool KernelStuck;
        public double CpuSeconds;
        public string Error;
        public string WaitChain;
        public bool WaitCycle;
        public List<string> WaitProcesses = new List<string>();
        public List<Frame> Frames = new List<Frame>();

        public bool HasModule(Func<string, bool> test)
        {
            return Frames.Any(f => f.Module != null && test(f.Module));
        }

        public string Key()
        {
            return string.Join("|", Frames.Take(8).Select(f => f.Address.ToString("x")).ToArray());
        }
    }

    sealed class HangCapture
    {
        public DateTime When;
        public bool UserRequested;
        public bool AcrWindowOpen;
        public bool AcrModuleLoaded;
        public string AcrModulePath;
        public string AcrVersion;
        public string PhotoshopPath;
        public string PhotoshopVersion;
        public int ProcessId;
        public double ProcessCpuPercent;
        public long WorkingSetMb;
        public double RamFreeGb;
        public bool StackStable;
        public bool StackCompared;
        public string Error;
        public string NvidiaSmi;
        public List<WinInfo> Windows = new List<WinInfo>();
        public List<ThreadStack> Threads = new List<ThreadStack>();
        public List<ModuleInfo> Modules = new List<ModuleInfo>();

        public IEnumerable<ThreadStack> UiThreads { get { return Threads.Where(t => t.IsUiThread); } }
    }

    sealed class Finding
    {
        // 3 = direct evidence, 2 = strong indication, 1 = worth checking.
        public int Confidence;
        public string Id;
        public string Title;
        public string Evidence;
        public string Advice;
        public string[] Fixes = new string[0];
    }

    sealed class Change
    {
        public DateTime When;
        public string Kind;
        public string What;
    }

    sealed class LogEvent
    {
        public DateTime When;
        public string Source;
        public int Id;
        public string Text;
    }

    static class Util
    {
        public static string AppData { get { return Environment.GetEnvironmentVariable("APPDATA") ?? ""; } }
        public static string LocalAppData { get { return Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? ""; } }

        public static string DataDir
        {
            get
            {
                string d = Path.Combine(LocalAppData, "FixPhotoshop");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        public static T Try<T>(Func<T> f, T fallback)
        {
            try { return f(); }
            catch (Exception) { return fallback; }
        }

        // Recursive listing that survives access-denied folders.
        public static List<string> WalkFiles(string root, int maxDepth)
        {
            var result = new List<string>();
            WalkInto(root, maxDepth, result);
            return result;
        }

        static void WalkInto(string dir, int depth, List<string> result)
        {
            if (depth < 0 || !Directory.Exists(dir)) return;
            try { result.AddRange(Directory.GetFiles(dir)); }
            catch (Exception) { }
            string[] subs;
            try { subs = Directory.GetDirectories(dir); }
            catch (Exception) { return; }
            foreach (string s in subs) WalkInto(s, depth - 1, result);
        }

        public static string Gb(long bytes)
        {
            return (bytes / 1073741824.0).ToString("0.0") + " ГБ";
        }

        public static string TailLines(string path, int count)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var sr = new StreamReader(fs))
                {
                    var all = sr.ReadToEnd().Split('\n');
                    return string.Join("\n", all.Skip(Math.Max(0, all.Length - count)).ToArray());
                }
            }
            catch (Exception ex) { return "(не удалось прочитать: " + ex.Message + ")"; }
        }

        public static string Oneline(string s)
        {
            if (s == null) return "";
            return System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
        }

        public static string Clip(string s, int max)
        {
            if (s == null) return "";
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }
    }
}
