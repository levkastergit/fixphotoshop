using System;
using System.Runtime.InteropServices;
using System.Text;

namespace FixPhotoshop
{
    static class Native
    {
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsHungAppWindow(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder s, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder s, int max);

        public const uint PROCESS_QUERY_INFORMATION = 0x0400;
        public const uint PROCESS_VM_READ = 0x0010;
        public const uint THREAD_SUSPEND_RESUME = 0x0002;
        public const uint THREAD_GET_CONTEXT = 0x0008;
        public const uint THREAD_QUERY_INFORMATION = 0x0040;

        [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenThread(uint access, bool inherit, uint tid);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern uint SuspendThread(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern uint ResumeThread(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GetThreadContext(IntPtr h, IntPtr ctx);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern IntPtr LoadLibrary(string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)] public static extern IntPtr GetProcAddress(IntPtr module, string name);

        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORYSTATUSEX
        {
            public uint dwLength, dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

        public const uint SYMOPT_UNDNAME = 0x2, SYMOPT_DEFERRED_LOADS = 0x4, SYMOPT_FAIL_CRITICAL_ERRORS = 0x200, SYMOPT_NO_PROMPTS = 0x80000;
        [DllImport("dbghelp.dll", SetLastError = true)] public static extern uint SymSetOptions(uint options);
        [DllImport("dbghelp.dll", SetLastError = true, CharSet = CharSet.Ansi)] public static extern bool SymInitialize(IntPtr hProcess, string searchPath, bool invade);
        [DllImport("dbghelp.dll", SetLastError = true)] public static extern bool SymCleanup(IntPtr hProcess);
        [DllImport("dbghelp.dll", SetLastError = true)] public static extern bool SymFromAddr(IntPtr hProcess, ulong address, out ulong displacement, IntPtr symbol);
        [DllImport("dbghelp.dll", SetLastError = true)]
        public static extern bool StackWalk64(uint machine, IntPtr hProcess, IntPtr hThread, IntPtr frame, IntPtr context,
            IntPtr readMemory, IntPtr functionTableAccess, IntPtr getModuleBase, IntPtr translateAddress);

        [DllImport("advapi32.dll", SetLastError = true)] public static extern IntPtr OpenThreadWaitChainSession(uint flags, IntPtr callback);
        [DllImport("advapi32.dll", SetLastError = true)] public static extern void CloseThreadWaitChainSession(IntPtr session);
        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool GetThreadWaitChain(IntPtr session, IntPtr context, uint flags, uint threadId, ref uint nodeCount, IntPtr nodes, out int isCycle);
    }
}
