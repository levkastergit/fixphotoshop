// Test double: a process named Photoshop.exe with a real window titled "Camera Raw ..." that really stops pumping messages.
// nvoglv64.dll and "Camera Raw.8bi" are renamed copies of msvcrt.dll, so the frozen stack genuinely contains those module names.
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

static class FakePhotoshop
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr LoadLibrary(string path);

    // _cwait keeps its own frame on the stack while it waits; waiting on our own process never returns.
    [DllImport("nvoglv64.dll", EntryPoint = "_cwait", CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr WaitInsideFakeDriver(IntPtr status, IntPtr process, int action);

    // A native wait, because Wait Chain Traversal does not see the CLR's managed Mutex.WaitOne.
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateMutex(IntPtr attributes, bool owned, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint WaitForSingleObject(IntPtr handle, uint ms);

    [MTAThread]
    static int Main(string[] args)
    {
        string scenario = args.Length > 0 ? args[0] : "ok";
        LoadLibrary(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Camera Raw.8bi"));

        var form = new Form { Text = "Camera Raw 18.7 - Test", Width = 420, Height = 200 };
        var timer = new System.Windows.Forms.Timer { Interval = 1500 };
        timer.Tick += (s, e) =>
        {
            timer.Stop();
            Freeze(scenario);
        };
        form.Shown += (s, e) => timer.Start();
        Application.Run(form);
        return 0;
    }

    static void Freeze(string scenario)
    {
        switch (scenario)
        {
            case "gpu":
                WaitInsideFakeDriver(IntPtr.Zero, System.Diagnostics.Process.GetCurrentProcess().Handle, 0);
                break;
            case "spin":
                while (true) { }
            case "deadlock":
                Deadlock();
                break;
        }
    }

    static void Deadlock()
    {
        const uint Infinite = 0xFFFFFFFF;
        IntPtr a = CreateMutex(IntPtr.Zero, false, null);
        IntPtr b = CreateMutex(IntPtr.Zero, false, null);
        WaitForSingleObject(a, Infinite);
        var other = new Thread(() =>
        {
            WaitForSingleObject(b, Infinite);
            Thread.Sleep(300);
            WaitForSingleObject(a, Infinite);
        });
        other.IsBackground = true;
        other.Start();
        Thread.Sleep(600);
        WaitForSingleObject(b, Infinite);
    }
}
