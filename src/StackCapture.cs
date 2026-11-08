using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace FixPhotoshop
{
    // Reads what a frozen process is doing: top-level windows, thread stacks and wait chains.
    // Every call that can block on a stuck driver runs on a helper thread with a timeout,
    // because a thread stuck in kernel mode can make GetThreadContext wait indefinitely.
    static class StackCapture
    {
        const int MaxFrames = 64;
        const int ThreadTimeoutMs = 6000;
        const int TotalBudgetMs = 60000;
        const int ContextSize = 1232;
        static readonly object DbgLock = new object();

        public static bool IsAcrTitle(string title)
        {
            return title != null && title.IndexOf("Camera Raw", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static List<WinInfo> ListWindows(int pid)
        {
            var result = new List<WinInfo>();
            Native.EnumWindows((hwnd, l) =>
            {
                uint owner;
                uint tid = Native.GetWindowThreadProcessId(hwnd, out owner);
                if (owner != pid || !Native.IsWindowVisible(hwnd)) return true;
                var title = new StringBuilder(512);
                var cls = new StringBuilder(256);
                Native.GetWindowText(hwnd, title, title.Capacity);
                Native.GetClassName(hwnd, cls, cls.Capacity);
                result.Add(new WinInfo
                {
                    Hwnd = hwnd,
                    ThreadId = tid,
                    Title = title.ToString(),
                    ClassName = cls.ToString(),
                    Hung = Native.IsHungAppWindow(hwnd)
                });
                return true;
            }, IntPtr.Zero);
            return result;
        }

        public static List<ModuleInfo> ReadModules(Process ps)
        {
            for (int attempt = 0; attempt < 4; attempt++)
            {
                try
                {
                    ps.Refresh();
                    var list = new List<ModuleInfo>();
                    foreach (ProcessModule m in ps.Modules)
                    {
                        var info = new ModuleInfo
                        {
                            Name = m.ModuleName,
                            FilePath = m.FileName,
                            Base = (ulong)m.BaseAddress.ToInt64()
                        };
                        info.End = info.Base + (ulong)m.ModuleMemorySize;
                        try
                        {
                            var v = m.FileVersionInfo;
                            info.Company = v.CompanyName;
                            info.Version = string.IsNullOrEmpty(v.ProductVersion) ? v.FileVersion : v.ProductVersion;
                        }
                        catch (Exception) { }
                        list.Add(info);
                    }
                    list.Sort((a, b) => a.Base.CompareTo(b.Base));
                    return list;
                }
                catch (Exception) { Thread.Sleep(250); }
            }
            return new List<ModuleInfo>();
        }

        static bool RunTimed(Action action, int ms)
        {
            var t = new Thread(() => { try { action(); } catch (Exception) { } });
            t.IsBackground = true;
            t.Start();
            return t.Join(ms);
        }

        static Dictionary<uint, double> SampleCpu(int pid)
        {
            var d = new Dictionary<uint, double>();
            try
            {
                using (var p = Process.GetProcessById(pid))
                {
                    foreach (ProcessThread t in p.Threads)
                    {
                        try { d[(uint)t.Id] = t.TotalProcessorTime.TotalSeconds; }
                        catch (Exception) { }
                    }
                }
            }
            catch (Exception) { }
            return d;
        }

        public static HangCapture Capture(Process ps, List<WinInfo> windows, bool userRequested)
        {
            var cap = new HangCapture
            {
                When = DateTime.Now,
                UserRequested = userRequested,
                ProcessId = ps.Id,
                Windows = windows,
                AcrWindowOpen = windows.Any(w => IsAcrTitle(w.Title))
            };

            cap.Modules = ReadModules(ps);
            var acr = cap.Modules.FirstOrDefault(m => Mods.IsAcr(m.Name));
            cap.AcrModuleLoaded = acr != null;
            if (acr != null)
            {
                cap.AcrModulePath = acr.FilePath;
                cap.AcrVersion = acr.Version;
            }
            var exe = cap.Modules.FirstOrDefault(m => m.Name.Equals("Photoshop.exe", StringComparison.OrdinalIgnoreCase));
            if (exe != null)
            {
                cap.PhotoshopPath = exe.FilePath;
                cap.PhotoshopVersion = exe.Version;
            }
            cap.WorkingSetMb = Util.Try(() => { ps.Refresh(); return ps.WorkingSet64 / 1048576; }, 0L);
            cap.RamFreeGb = Snapshot.FreeRamGb();

            var hungTids = new HashSet<uint>(windows.Where(w => w.Hung).Select(w => w.ThreadId));
            var uiTids = hungTids.Count > 0 ? hungTids : new HashSet<uint>(windows.Select(w => w.ThreadId));

            IntPtr hProc = Native.OpenProcess(Native.PROCESS_QUERY_INFORMATION | Native.PROCESS_VM_READ, false, ps.Id);
            if (hProc == IntPtr.Zero)
            {
                cap.Error = "Нет доступа к процессу Photoshop (код " + Marshal.GetLastWin32Error() +
                    "). Если Photoshop запущен от имени администратора, запустите FixPhotoshop тоже от имени администратора.";
                return cap;
            }

            bool symOk = false;
            try
            {
                var before = SampleCpu(ps.Id);
                double procBefore = Util.Try(() => ps.TotalProcessorTime.TotalSeconds, 0.0);
                var sw = Stopwatch.StartNew();
                Thread.Sleep(3000);
                var after = SampleCpu(ps.Id);
                double procAfter = Util.Try(() => ps.TotalProcessorTime.TotalSeconds, 0.0);
                cap.ProcessCpuPercent = 100.0 * (procAfter - procBefore) / (sw.Elapsed.TotalSeconds * Environment.ProcessorCount);

                IntPtr dbg = Native.LoadLibrary("dbghelp.dll");
                IntPtr funcTable = Native.GetProcAddress(dbg, "SymFunctionTableAccess64");
                IntPtr modBase = Native.GetProcAddress(dbg, "SymGetModuleBase64");
                Native.SymSetOptions(Native.SYMOPT_UNDNAME | Native.SYMOPT_DEFERRED_LOADS | Native.SYMOPT_FAIL_CRITICAL_ERRORS | Native.SYMOPT_NO_PROMPTS);
                bool initDone = RunTimed(() => { symOk = Native.SymInitialize(hProc, null, true); }, 20000);
                if (!initDone || !symOk)
                    cap.Error = "Не удалось подготовить чтение стеков (dbghelp). Часть данных недоступна.";

                IntPtr wct = Native.OpenThreadWaitChainSession(0, IntPtr.Zero);
                var budget = Stopwatch.StartNew();
                var ordered = after.Keys
                    .OrderByDescending(t => uiTids.Contains(t))
                    .ThenByDescending(t => after[t] - (before.ContainsKey(t) ? before[t] : after[t]))
                    .ToList();

                foreach (uint tid in ordered)
                {
                    bool isUi = uiTids.Contains(tid);
                    if (!isUi && budget.ElapsedMilliseconds > TotalBudgetMs) break;

                    ThreadStack st = (symOk && modBase != IntPtr.Zero)
                        ? Walk(hProc, tid, cap.Modules, funcTable, modBase)
                        : new ThreadStack { ThreadId = tid };
                    st.IsUiThread = isUi;
                    st.CpuSeconds = after[tid] - (before.ContainsKey(tid) ? before[tid] : after[tid]);

                    if (wct != IntPtr.Zero && (isUi || st.CpuSeconds > 0.5))
                    {
                        ThreadStack target = st;
                        int pid = ps.Id;
                        if (!RunTimed(() => FillWaitChain(wct, target, pid), 4000))
                            st.WaitChain = "нет ответа за 4 с";
                    }
                    cap.Threads.Add(st);
                }
                if (wct != IntPtr.Zero) Native.CloseThreadWaitChainSession(wct);

                // A second look tells a thread that sits still from one that keeps moving.
                if (symOk && modBase != IntPtr.Zero)
                {
                    Thread.Sleep(1500);
                    foreach (var st in cap.Threads.Where(t => t.IsUiThread && t.Frames.Count > 0).ToList())
                    {
                        var again = Walk(hProc, st.ThreadId, cap.Modules, funcTable, modBase);
                        if (again.Frames.Count == 0) continue;
                        cap.StackCompared = true;
                        cap.StackStable = again.Key() == st.Key();
                    }
                }
            }
            finally
            {
                if (symOk) RunTimed(() => Native.SymCleanup(hProc), 5000);
                Native.CloseHandle(hProc);
            }
            return cap;
        }

        static ThreadStack Walk(IntPtr hProc, uint tid, List<ModuleInfo> mods, IntPtr funcTable, IntPtr modBase)
        {
            var job = new WalkJob { Proc = hProc, Tid = tid, Mods = mods, FuncTable = funcTable, ModBase = modBase };
            var t = new Thread(job.Run);
            t.IsBackground = true;
            t.Start();
            if (!t.Join(ThreadTimeoutMs))
            {
                job.Abandoned = true;
                job.Result.KernelStuck = !job.Result.GotContext;
                job.Result.Error = job.Result.KernelStuck
                    ? "Контекст потока не получен за " + (ThreadTimeoutMs / 1000) + " с: поток застрял в режиме ядра"
                    : "Разбор стека не уложился в " + (ThreadTimeoutMs / 1000) + " с";
            }
            return job.Result;
        }

        sealed class WalkJob
        {
            public IntPtr Proc, FuncTable, ModBase;
            public uint Tid;
            public List<ModuleInfo> Mods;
            public volatile bool Abandoned;
            public readonly ThreadStack Result = new ThreadStack();

            public void Run()
            {
                Result.ThreadId = Tid;
                IntPtr h = Native.OpenThread(Native.THREAD_SUSPEND_RESUME | Native.THREAD_GET_CONTEXT | Native.THREAD_QUERY_INFORMATION, false, Tid);
                if (h == IntPtr.Zero)
                {
                    Result.Error = "OpenThread: код " + Marshal.GetLastWin32Error();
                    return;
                }
                IntPtr raw = Marshal.AllocHGlobal(ContextSize + 32);
                IntPtr frame = Marshal.AllocHGlobal(512);
                IntPtr sym = Marshal.AllocHGlobal(88 + 520);
                bool suspended = false;
                try
                {
                    if (Native.SuspendThread(h) == 0xFFFFFFFF)
                    {
                        Result.Error = "SuspendThread: код " + Marshal.GetLastWin32Error();
                        return;
                    }
                    suspended = true;

                    // CONTEXT must be 16-byte aligned on x64.
                    IntPtr ctx = new IntPtr((raw.ToInt64() + 15) & ~15L);
                    Zero(ctx, ContextSize);
                    Marshal.WriteInt32(ctx, 0x30, 0x0010000B); // CONTEXT_FULL | CONTEXT_AMD64
                    if (!Native.GetThreadContext(h, ctx))
                    {
                        Result.Error = "GetThreadContext: код " + Marshal.GetLastWin32Error();
                        return;
                    }
                    Result.GotContext = true;
                    if (Abandoned) return;
                    lock (DbgLock) WalkFrames(h, ctx, frame, sym);
                }
                finally
                {
                    if (suspended) Native.ResumeThread(h);
                    Native.CloseHandle(h);
                    Marshal.FreeHGlobal(raw);
                    Marshal.FreeHGlobal(frame);
                    Marshal.FreeHGlobal(sym);
                }
            }

            void WalkFrames(IntPtr hThread, IntPtr ctx, IntPtr frame, IntPtr sym)
            {
                const int AddrModeFlat = 3;
                Zero(frame, 512);
                Marshal.WriteInt64(frame, 0, Marshal.ReadInt64(ctx, 0xF8));   // AddrPC = Rip
                Marshal.WriteInt32(frame, 12, AddrModeFlat);
                Marshal.WriteInt64(frame, 32, Marshal.ReadInt64(ctx, 0xA0));  // AddrFrame = Rbp
                Marshal.WriteInt32(frame, 44, AddrModeFlat);
                Marshal.WriteInt64(frame, 48, Marshal.ReadInt64(ctx, 0x98));  // AddrStack = Rsp
                Marshal.WriteInt32(frame, 60, AddrModeFlat);

                var frames = new List<Frame>();
                for (int i = 0; i < MaxFrames && !Abandoned; i++)
                {
                    if (!Native.StackWalk64(0x8664, Proc, hThread, frame, ctx, IntPtr.Zero, FuncTable, ModBase, IntPtr.Zero)) break;
                    ulong pc = (ulong)Marshal.ReadInt64(frame, 0);
                    if (pc == 0) break;
                    frames.Add(Resolve(pc, sym));
                }
                if (!Abandoned) Result.Frames = frames;
            }

            Frame Resolve(ulong pc, IntPtr sym)
            {
                var f = new Frame { Address = pc };
                ModuleInfo m = Mods.FirstOrDefault(x => pc >= x.Base && pc < x.End);
                if (m != null)
                {
                    f.Module = m.Name;
                    f.Offset = pc - m.Base;
                }
                Zero(sym, 88);
                Marshal.WriteInt32(sym, 0, 88);    // SizeOfStruct
                Marshal.WriteInt32(sym, 80, 512);  // MaxNameLen
                ulong displacement;
                if (Native.SymFromAddr(Proc, pc, out displacement, sym))
                {
                    int len = Marshal.ReadInt32(sym, 76);
                    if (len > 0)
                    {
                        f.Symbol = Marshal.PtrToStringAnsi(IntPtr.Add(sym, 84), Math.Min(len, 512));
                        f.Offset = displacement;
                    }
                }
                return f;
            }

            static void Zero(IntPtr p, int n)
            {
                Marshal.Copy(new byte[n], 0, p, n);
            }
        }

        static void FillWaitChain(IntPtr session, ThreadStack st, int ownPid)
        {
            const int NodeSize = 280, NodeCount = 16;
            const uint Flags = 1 | 4; // WCT_OUT_OF_PROC_FLAG | WCT_OUT_OF_PROC_CS_FLAG
            IntPtr buf = Marshal.AllocHGlobal(NodeSize * NodeCount);
            try
            {
                uint n = NodeCount;
                int cycle;
                if (!Native.GetThreadWaitChain(session, IntPtr.Zero, Flags, st.ThreadId, ref n, buf, out cycle))
                {
                    st.WaitChain = "недоступно (код " + Marshal.GetLastWin32Error() + ")";
                    return;
                }
                st.WaitCycle = cycle != 0;
                var parts = new List<string>();
                for (int i = 0; i < n; i++)
                {
                    IntPtr p = IntPtr.Add(buf, i * NodeSize);
                    int type = Marshal.ReadInt32(p, 0);
                    int status = Marshal.ReadInt32(p, 4);
                    if (type == 8) // WctThreadType
                    {
                        int pid = Marshal.ReadInt32(p, 8);
                        int tid = Marshal.ReadInt32(p, 12);
                        string name = Util.Try(() => { using (var x = Process.GetProcessById(pid)) return x.ProcessName; }, "pid " + pid);
                        if (pid != ownPid && !st.WaitProcesses.Contains(name)) st.WaitProcesses.Add(name);
                        parts.Add(string.Format("поток {0} [{1}, {2}]", tid, name, StatusName(status)));
                    }
                    else
                    {
                        string obj = Marshal.PtrToStringUni(IntPtr.Add(p, 8));
                        parts.Add(TypeName(type) + (string.IsNullOrEmpty(obj) ? "" : " «" + obj + "»"));
                    }
                }
                st.WaitChain = string.Join(" -> ", parts.ToArray());
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        static string StatusName(int s)
        {
            switch (s)
            {
                case 2: return "работает";
                case 3: return "заблокирован";
                case 1: return "нет доступа";
                default: return "состояние " + s;
            }
        }

        static string TypeName(int t)
        {
            switch (t)
            {
                case 1: return "критическая секция";
                case 2: return "SendMessage";
                case 3: return "мьютекс";
                case 4: return "ALPC";
                case 5: return "COM";
                case 6: return "ожидание потока";
                case 7: return "ожидание процесса";
                case 9: return "активация COM";
                case 11: return "сокет";
                case 12: return "SMB";
                default: return "объект " + t;
            }
        }
    }

    // Module-name knowledge used to interpret a stack.
    static class Mods
    {
        static readonly HashSet<string> Nvidia = Set("nvoglv64.dll", "nvoglv32.dll", "nvwgf2umx.dll", "nvwgf2um.dll", "nvldumdx.dll", "nvldumd.dll",
            "nvd3dumx.dll", "nvd3dum.dll", "nvopencl64.dll", "nvopencl.dll", "nvcuda.dll", "nvapi64.dll", "nvapi.dll", "nvgpucomp64.dll", "nvgpucomp32.dll");
        static readonly HashSet<string> Amd = Set("atio6axx.dll", "atiumd64.dll", "atidxx64.dll", "amdocl64.dll", "amdxx64.dll");
        static readonly HashSet<string> Intel = Set("igd10iumd64.dll", "igd12umd64.dll", "igdumdim64.dll", "igc64.dll", "igxelpicd64.dll", "ig9icd64.dll");
        static readonly HashSet<string> GfxApi = Set("d3d11.dll", "d3d12.dll", "d3d12core.dll", "dxgi.dll", "dxcore.dll", "opengl32.dll", "opencl.dll",
            "vulkan-1.dll", "directml.dll", "d3dcompiler_47.dll");
        static readonly HashSet<string> Ai = Set("onnxruntime.dll", "directml.dll");
        static readonly HashSet<string> Net = Set("winhttp.dll", "wininet.dll", "ws2_32.dll", "mswsock.dll", "dnsapi.dll", "urlmon.dll");
        static readonly HashSet<string> Print = Set("winspool.drv", "spoolss.dll", "printconfig.dll");

        // Well-known hook DLLs that overlay/capture software injects into other programs, and the processes behind them.
        public static readonly Dictionary<string, string> Overlays = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "rtsshooks64.dll", "RivaTuner Statistics Server / MSI Afterburner" },
            { "gameoverlayrenderer64.dll", "Steam Overlay" },
            { "discordhook64.dll", "Discord Overlay" },
            { "graphics-hook64.dll", "OBS Studio" },
            { "reshade64.dll", "ReShade" },
            { "nvspcap64.dll", "NVIDIA Overlay / ShadowPlay" },
            { "nahimicosd.dll", "Nahimic" }
        };

        public static readonly Dictionary<string, string[]> OverlayProcesses = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "rtsshooks64.dll", new[] { "RTSS", "RTSSHooksLoader64", "MSIAfterburner" } },
            { "gameoverlayrenderer64.dll", new[] { "steam" } },
            { "discordhook64.dll", new[] { "Discord" } },
            { "graphics-hook64.dll", new[] { "obs64" } },
            { "nvspcap64.dll", new[] { "NVIDIA Overlay", "NVIDIA Share", "nvsphelper64" } }
        };

        static HashSet<string> Set(params string[] items)
        {
            return new HashSet<string>(items, StringComparer.OrdinalIgnoreCase);
        }

        public static bool IsNvidia(string m) { return Nvidia.Contains(m); }
        public static bool IsAmdOrIntel(string m) { return Amd.Contains(m) || Intel.Contains(m); }
        public static bool IsGpuDriver(string m) { return IsNvidia(m) || IsAmdOrIntel(m); }
        public static bool IsGfxApi(string m) { return GfxApi.Contains(m); }
        public static bool IsGpuAny(string m) { return IsGpuDriver(m) || IsGfxApi(m); }
        public static bool IsAi(string m) { return Ai.Contains(m); }
        public static bool IsAcr(string m) { return m.StartsWith("Camera Raw", StringComparison.OrdinalIgnoreCase); }
        public static bool IsNet(string m) { return Net.Contains(m); }
        public static bool IsPrint(string m) { return Print.Contains(m); }

        public static bool IsThirdParty(ModuleInfo m)
        {
            string path = (m.FilePath ?? "").ToLowerInvariant();
            string windir = (Environment.GetEnvironmentVariable("WINDIR") ?? @"C:\Windows").ToLowerInvariant();
            if (path.StartsWith(windir)) return false;
            if (path.Contains(@"\adobe\")) return false;
            string company = (m.Company ?? "").ToLowerInvariant();
            return !(company.Contains("adobe") || company.Contains("microsoft") || company.Contains("nvidia"));
        }
    }
}
