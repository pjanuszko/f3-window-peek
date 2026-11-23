using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

internal static class Program
{
    internal const string WindowTitle = "F3 Window Peek Controller 40eae357";
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--verify-desktops") return DesktopSwitchVerification.Run(args[1]);
        if (args.Length == 1 && args[0] == "--stop")
        {
            IntPtr window = Native.FindWindow(null, WindowTitle);
            if (window != IntPtr.Zero) Native.PostMessage(window, Native.StopMessage, IntPtr.Zero, IntPtr.Zero);
            return 0;
        }
        bool first;
        using (var mutex = new Mutex(true, @"Local\F3WindowPeek-40eae357", out first))
        {
            if (!first) return 0;
            Native.SetProcessDpiAwarenessContext(new IntPtr(-4));
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                using (var app = new PeekContext(args)) Application.Run(app);
                return 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "F3 Window Peek", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
    }
}

internal sealed class PeekContext : ApplicationContext
{
    private readonly ControllerWindow window;
    private readonly NotifyIcon tray;
    private readonly System.Windows.Forms.Timer timer;
    private readonly PreviewSession preview;
    private readonly FastPreview fastPreview;
    private readonly Native.KeyboardProc keyboardProc;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly string logPath;
    private IntPtr hook;
    private bool enabled = true;
    private bool capturedF3;
    private bool passingF3;
    private bool disposed;
    private bool exiting;
    private int demoDuration;
    private long demoStarted = -1;
    private bool demoReleased;
    private int demoRepeats;
    private bool demoPrepared;
    private long pressTimestamp;
    private bool fastMode = true;
    private bool fastGesture;
    private bool capturedEscape;
    private readonly string modePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "mode.txt");
    private int demoCount = 1;
    private int demoCompleted;
    private long demoNextStart = 600;

    internal PeekContext(string[] args)
    {
        if (File.Exists(modePath)) fastMode = File.ReadAllText(modePath).Trim() != "native";
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--log" && i + 1 < args.Length) logPath = Path.GetFullPath(args[++i]);
            else if (args[i] == "--native") fastMode = false;
            else if (args[i] == "--fast") fastMode = true;
            else if (args[i] == "--inspect") PreviewMonitor.Inspectable = true;
            else if (args[i] == "--demo-count" && i + 1 < args.Length)
            {
                demoCount = int.Parse(args[++i]);
                if (demoCount < 1 || demoCount > 20) throw new ArgumentException("Demo count: 1-20.");
            }
            else if (args[i] == "--demo" && i + 1 < args.Length)
            {
                demoDuration = int.Parse(args[++i]);
                if (demoDuration < 10 || demoDuration > 30000) throw new ArgumentException("Demo: 10-30000 ms.");
            }
            else throw new ArgumentException("Opcje: --stop | --demo <ms> | --log <plik> | --fast | --native.");
        }

        window = new ControllerWindow(delegate { ExitThread(); });
        window.ApplicationContextOwner = this;
        preview = new PreviewSession(Native.ForegroundTaskView, Native.ModifiersDown,
            Native.OpenTaskView, Native.CloseTaskView, delegate { return clock.ElapsedMilliseconds; }, Trace);
        fastPreview = new FastPreview(Trace);
        timer = new System.Windows.Forms.Timer { Interval = 15 };
        timer.Tick += delegate
        {
            if (demoDuration > 0)
            {
                if (!demoPrepared)
                {
                    demoPrepared = true;
                    // A previous interrupted test may have left its overview open.
                    Native.CloseTaskView();
                }
                if (demoStarted < 0 && clock.ElapsedMilliseconds >= demoNextStart)
                {
                    demoStarted = clock.ElapsedMilliseconds;
                    Native.DemoF3(false);
                }
                if (demoStarted >= 0 && !demoReleased && demoDuration > 1500
                    && clock.ElapsedMilliseconds - demoStarted >= (demoRepeats + 1) * 300
                    && demoRepeats < 3)
                {
                    demoRepeats++;
                    Native.DemoF3(false);
                }
                if (demoStarted >= 0 && !demoReleased && clock.ElapsedMilliseconds - demoStarted >= demoDuration)
                {
                    demoReleased = true;
                    Native.DemoF3(true);
                }
                if (demoReleased && !preview.Busy && !fastPreview.Visible)
                {
                    if (++demoCompleted >= demoCount) { ExitThread(); return; }
                    demoStarted = -1;
                    demoReleased = false;
                    demoRepeats = 0;
                    demoNextStart = clock.ElapsedMilliseconds + 400;
                }
            }
            preview.Tick();
            if (!preview.Busy && demoDuration == 0) timer.Stop();
        };

        var menu = new ContextMenuStrip();
        var toggle = new ToolStripMenuItem("Włączony — przytrzymaj F3") { Checked = true };
        toggle.Click += delegate { SetEnabled(!enabled); toggle.Checked = enabled; };
        menu.Items.Add(toggle);
        var fastOption = new ToolStripMenuItem("Szybki podgląd — gotowe miniatury") { Checked = fastMode };
        var nativeOption = new ToolStripMenuItem("Widok Windows — Win+Tab") { Checked = !fastMode };
        fastOption.Click += delegate { ChangeMode(true); fastOption.Checked = true; nativeOption.Checked = false; };
        nativeOption.Click += delegate { ChangeMode(false); fastOption.Checked = false; nativeOption.Checked = true; };
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(fastOption);
        menu.Items.Add(nativeOption);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Zakończ", null, delegate { ExitThread(); });
        tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "F3: przytrzymaj — podgląd okien",
            ContextMenuStrip = menu,
            Visible = true
        };
        tray.DoubleClick += delegate { SetEnabled(!enabled); toggle.Checked = enabled; };

        keyboardProc = KeyboardHook;
        hook = Native.SetWindowsHookEx(13, keyboardProc, Native.GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        Trace("ready");
        if (demoDuration > 0) timer.Start();
    }

    private IntPtr KeyboardHook(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            int key = Marshal.ReadInt32(data);
            int kind = message.ToInt32();
            bool down = kind == 0x100 || kind == 0x104;
            bool up = kind == 0x101 || kind == 0x105;
            if (key == 0x1B && down && capturedEscape) return new IntPtr(1);
            if (key == 0x1B && down && fastPreview.Visible)
            {
                capturedEscape = true;
                Native.PostMessage(window.Handle, Native.CancelMessage, new IntPtr(1), IntPtr.Zero);
                return new IntPtr(1);
            }
            if (key == 0x1B && up && capturedEscape) { capturedEscape = false; return new IntPtr(1); }
            if (down && fastPreview.Visible && (key == 0x5B || key == 0x5C || key == 0x12 || key == 0xA4 || key == 0xA5))
                Native.PostMessage(window.Handle, Native.CancelMessage, IntPtr.Zero, IntPtr.Zero);
        }
        if (code >= 0 && Marshal.ReadInt32(data) == 0x72) // VK_F3
        {
            int kind = message.ToInt32();
            bool down = kind == 0x100 || kind == 0x104;
            bool up = kind == 0x101 || kind == 0x105;
            if (down)
            {
                if (capturedF3) return new IntPtr(1);
                if (passingF3) return Native.CallNextHookEx(hook, code, message, data);
                if (enabled && !Native.ModifiersDown())
                {
                    capturedF3 = true;
                    pressTimestamp = Stopwatch.GetTimestamp();
                    // No shell operations or disk I/O inside the keyboard hook.
                    Native.PostMessage(window.Handle, Native.PressMessage, IntPtr.Zero, IntPtr.Zero);
                    return new IntPtr(1);
                }
                passingF3 = true;
            }
            if (up && capturedF3)
            {
                capturedF3 = false;
                Native.PostMessage(window.Handle, Native.ReleaseMessage, IntPtr.Zero, IntPtr.Zero);
                return new IntPtr(1);
            }
            if (up) passingF3 = false;
        }
        return Native.CallNextHookEx(hook, code, message, data);
    }

    private void SetEnabled(bool value)
    {
        enabled = value;
        if (!enabled) { fastPreview.HidePreview(false); preview.Release(); timer.Start(); }
        tray.Text = enabled ? "F3: przytrzymaj — podgląd okien" : "F3 Window Peek — pauza";
    }

    internal void OnPress()
    {
        if (!enabled) return;
        double queueMs = (Stopwatch.GetTimestamp() - pressTimestamp) * 1000.0 / Stopwatch.Frequency;
        var dispatchClock = Stopwatch.StartNew();
        fastGesture = fastMode;
        if (fastGesture) fastPreview.ShowPreview();
        else { preview.Press(); timer.Start(); }
        Trace("f3-dispatch queueMs=" + queueMs.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
            + " launchMs=" + dispatchClock.Elapsed.TotalMilliseconds.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
    }

    internal void OnRelease()
    {
        if (fastGesture)
        {
            fastPreview.ReleasePreview();
            fastGesture = false;
            return;
        }
        preview.Release();
        if (preview.Busy) timer.Start();
    }

    private void ChangeMode(bool fast)
    {
        fastPreview.HidePreview(false);
        preview.Release();
        if (preview.Busy) timer.Start();
        fastMode = fast;
        try { File.WriteAllText(modePath, fast ? "fast" : "native"); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal void CancelFastPreview(bool animate) { fastPreview.HidePreview(animate); }

    private void Trace(string text)
    {
        if (logPath == null) return;
        try { File.AppendAllText(logPath, clock.ElapsedMilliseconds + " " + text + " " + Native.ForegroundDescription() + Environment.NewLine); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    protected override void ExitThreadCore()
    {
        if (exiting) return;
        exiting = true;
        if (demoStarted >= 0 && !demoReleased)
        {
            demoReleased = true;
            Native.DemoF3(true);
        }
        fastPreview.HidePreview(false);
        // Keep polling briefly if F3 was released during the opening animation.
        preview.Release();
        var cleanupClock = Stopwatch.StartNew();
        while (preview.Busy && cleanupClock.ElapsedMilliseconds < 1200)
        {
            preview.Tick();
            Application.DoEvents();
            Thread.Sleep(15);
        }
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposed)
        {
            disposed = true;
            if (hook != IntPtr.Zero) { Native.UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
            if (disposing)
            {
                timer.Dispose();
                fastPreview.Dispose();
                tray.Visible = false;
                tray.ContextMenuStrip.Dispose();
                tray.Dispose();
                window.DestroyHandle();
            }
        }
        base.Dispose(disposing);
    }

    private sealed class ControllerWindow : NativeWindow
    {
        private readonly Action stop;
        internal ControllerWindow(Action stop)
        {
            this.stop = stop;
            CreateHandle(new CreateParams { Caption = Program.WindowTitle, Style = 0, ExStyle = 0x80 });
        }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == Native.StopMessage) { stop(); return; }
            var owner = ApplicationContextOwner;
            if (owner != null && message.Msg == Native.PressMessage) { owner.OnPress(); return; }
            if (owner != null && message.Msg == Native.ReleaseMessage) { owner.OnRelease(); return; }
            if (owner != null && message.Msg == Native.CancelMessage) { owner.CancelFastPreview(message.WParam != IntPtr.Zero); return; }
            base.WndProc(ref message);
        }
        internal PeekContext ApplicationContextOwner;
    }
}

internal static class Native
{
    internal const int PressMessage = 0x8001;
    internal const int ReleaseMessage = 0x8002;
    internal const int CancelMessage = 0x8003;
    internal static readonly uint StopMessage = RegisterWindowMessage("F3WindowPeek.Stop.40eae357");
    internal delegate IntPtr KeyboardProc(int code, IntPtr message, IntPtr data);

    internal static bool ModifiersDown()
    {
        return Down(0x10) || Down(0x11) || Down(0x12) || Down(0x5B) || Down(0x5C);
    }
    private static bool Down(int key) { return (GetAsyncKeyState(key) & 0x8000) != 0; }

    internal static IntPtr ForegroundTaskView()
    {
        IntPtr window = GetForegroundWindow();
        if (window == IntPtr.Zero) return IntPtr.Zero;
        var className = new StringBuilder(256);
        GetClassName(window, className, className.Capacity);
        string cls = className.ToString();
        if (cls != "XamlExplorerHostIslandWindow" && cls != "MultitaskingViewFrame"
            && cls != "Windows.UI.Core.CoreWindow") return IntPtr.Zero;
        var title = new StringBuilder(256);
        GetWindowText(window, title, title.Capacity);
        // XamlExplorerHostIslandWindow is also used by Alt+Tab. Check the title.
        string name = title.ToString();
        if (!name.Equals("Task View", StringComparison.OrdinalIgnoreCase)
            && !name.Equals("Widok zadań", StringComparison.OrdinalIgnoreCase)) return IntPtr.Zero;
        uint pid;
        GetWindowThreadProcessId(window, out pid);
        try
        {
            using (var process = Process.GetProcessById((int)pid))
                return process.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase)
                    || process.ProcessName.Equals("ShellExperienceHost", StringComparison.OrdinalIgnoreCase)
                    ? window : IntPtr.Zero;
        }
        catch (ArgumentException) { return IntPtr.Zero; }
        catch (InvalidOperationException) { return IntPtr.Zero; }
    }

    internal static void OpenTaskView()
    {
        SendKeys(new[] { Key(0x5B, false), Key(0x09, false), Key(0x09, true), Key(0x5B, true) });
    }
    internal static void CloseTaskView()
    {
        // Re-check immediately before injection, including modifier state.
        if (ForegroundTaskView() != IntPtr.Zero && !ModifiersDown())
            SendKeys(new[] { Key(0x1B, false), Key(0x1B, true) });
    }
    // Integration-test input goes through the exact same hook as a real key.
    internal static void DemoF3(bool up) { SendKeys(new[] { Key(0x72, up) }); }
    internal static string ForegroundDescription()
    {
        IntPtr window = GetForegroundWindow();
        var cls = new StringBuilder(256);
        var title = new StringBuilder(256);
        GetClassName(window, cls, cls.Capacity);
        GetWindowText(window, title, title.Capacity);
        return "hwnd=" + window.ToInt64() + " class=" + cls + " title=" + title;
    }
    private static INPUT Key(ushort vk, bool up)
    {
        var value = new INPUT { type = 1 };
        value.data.keyboard.vk = vk;
        value.data.keyboard.flags = (up ? 2u : 0u) | (vk == 0x5B ? 1u : 0u);
        return value;
    }
    private static void SendKeys(INPUT[] keys)
    {
        if (SendInput((uint)keys.Length, keys, Marshal.SizeOf(typeof(INPUT))) != keys.Length)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows nie przyjął skrótu klawiaturowego.");
    }

    [StructLayout(LayoutKind.Sequential)] private struct INPUT { internal uint type; internal INPUTUNION data; }
    [StructLayout(LayoutKind.Explicit)] private struct INPUTUNION
    {
        [FieldOffset(0)] internal KEYBDINPUT keyboard;
        [FieldOffset(0)] internal MOUSEINPUT mouse;
    }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT
    {
        internal ushort vk, scan;
        internal uint flags, time;
        internal UIntPtr extra;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT
    {
        internal int dx, dy;
        internal uint mouseData, flags, time;
        internal UIntPtr extra;
    }

    [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr SetWindowsHookEx(int hook, KeyboardProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] internal static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] internal static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr GetModuleHandle(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll")] internal static extern bool PostMessage(IntPtr window, uint msg, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] internal static extern bool SetProcessDpiAwarenessContext(IntPtr context);
}
