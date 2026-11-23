using System;
using System.Runtime.InteropServices;
using System.Threading;

// A sleeping high-resolution clock posts coalesced frames to the UI thread.
// It runs only during transitions; unlike WM_TIMER it is not capped at ~60 Hz.
internal sealed class AnimationPulse : IDisposable
{
    private readonly AutoResetEvent wake = new AutoResetEvent(false);
    private readonly Thread worker;
    private readonly IntPtr window;
    private readonly uint message;
    private volatile bool active;
    private volatile bool disposed;
    private int pending;

    internal AnimationPulse(IntPtr window, uint message)
    {
        this.window = window;
        this.message = message;
        worker = new Thread(Run) { IsBackground = true, Name = "F3 animation clock", Priority = ThreadPriority.AboveNormal };
        worker.Start();
    }
    internal void Start() { active = true; wake.Set(); }
    internal void Stop() { active = false; }
    internal void Acknowledge() { Interlocked.Exchange(ref pending, 0); }

    private void Run()
    {
        IntPtr timer = CreateWaitableTimerEx(IntPtr.Zero, null, 2, 0x1F0003);
        try
        {
            while (!disposed)
            {
                wake.WaitOne();
                while (active && !disposed)
                {
                    long due = -50000; // 5 ms, one-shot: compatible with high-resolution timers.
                    if (timer != IntPtr.Zero && SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
                        WaitForSingleObject(timer, 100);
                    else Thread.Sleep(5);
                    if (active && !disposed && Interlocked.CompareExchange(ref pending, 1, 0) == 0)
                        if (!Native.PostMessage(window, message, IntPtr.Zero, IntPtr.Zero)) Interlocked.Exchange(ref pending, 0);
                }
            }
        }
        finally { if (timer != IntPtr.Zero) CloseHandle(timer); }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        active = false;
        wake.Set();
        if (worker.Join(300)) wake.Dispose();
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWaitableTimerEx(IntPtr attributes, string name, uint flags, uint access);
    [DllImport("kernel32.dll")] private static extern bool SetWaitableTimer(IntPtr timer, ref long due, int period, IntPtr completion, IntPtr argument, bool resume);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint timeout);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
