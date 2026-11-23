using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using System.Reflection;

internal static class SelectionVerification
{
    private static TextWriter output;
    private static int checks;
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--window-helper")
        {
            Application.EnableVisualStyles();
            using (var helper = new Form { Text = args[1], StartPosition = FormStartPosition.Manual,
                Bounds = new Rectangle(100, 120, 640, 420), BackColor = Color.FromArgb(46, 92, 133) })
                Application.Run(helper);
            return 0;
        }
        output = args.Length > 0 ? new StreamWriter(args[0], false) { AutoFlush = true } : Console.Out;
        IntPtr originalFocus = PreviewNative.GetForegroundWindow();
        Native.SetProcessDpiAwarenessContext(new IntPtr(-4));
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        try
        {
            RunCase("normal", false, false);
            RunCase("minimized", true, false);
            RunCase("cancel", false, true);
            RunCloseCase(false);
            RunCloseCase(true);
            RunDragCases();
            output.WriteLine("PASS: all " + checks + " selection, closing and dragging checks.");
            return 0;
        }
        catch (Exception ex) { output.WriteLine("FAIL: " + ex); return 1; }
        finally
        {
            if (PreviewNative.IsWindow(originalFocus)) PreviewNative.SetForegroundWindow(originalFocus);
            if (output != Console.Out) output.Dispose();
        }
    }

    private static void RunCase(string name, bool minimized, bool cancel)
    {
        var events = new List<string>();
        Rectangle screen = Screen.PrimaryScreen.WorkingArea;
        using (var target = new Form
        {
            Text = "F3 selection check — chosen window", BackColor = Color.FromArgb(49, 91, 145),
            StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(screen.Left + 80, screen.Top + 80, 620, 410)
        })
        using (var previous = new Form
        {
            Text = "F3 selection check — previous window", BackColor = Color.FromArgb(61, 72, 89),
            StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(screen.Left + 740, screen.Top + 100, 380, 220)
        })
        {
            target.Show();
            previous.Show();
            Pump(100);
            Rectangle original = target.Bounds;
            if (minimized) { target.WindowState = FormWindowState.Minimized; Pump(100); }
            previous.Activate();
            using (var preview = new FastPreview(delegate(string message) { events.Add(message); output.WriteLine(name + " " + message); }, true))
            {
                preview.ShowPreview();
                Pump(450);
                preview.SelectWindow(target.Handle);
                Assert(preview.Visible, name + ": overview remains until zoom completes");
                if (!minimized) Assert(PreviewNative.GetForegroundWindow() != target.Handle, name + ": no early focus switch");
                Pump(80);
                preview.ReleasePreview();
                Assert(preview.Visible, name + ": release F3 preserves committed selection");
                if (cancel) preview.HidePreview(false);
                Pump(450);
                Assert(!preview.Visible, name + ": all monitors dismiss their overview");
                bool focused = events.Exists(delegate(string message) { return message == "fast-selected"; });
                if (cancel)
                {
                    Assert(!focused, "cancel: no delayed focus after cancellation");
                    Assert(PreviewNative.GetForegroundWindow() != target.Handle, "cancel: chosen app was not activated");
                }
                else
                {
                    Assert(focused && PreviewNative.GetForegroundWindow() == target.Handle, name + ": activates chosen window after zoom");
                    Assert(!PreviewNative.IsIconic(target.Handle), name + ": chosen window is restored");
                    Assert(target.Bounds == original, name + ": preserves actual size and position");
                    int completedMonitors = 0;
                    foreach (string entry in events)
                    {
                        if (!entry.StartsWith("animation-select ")) continue;
                        Match match = Regex.Match(entry, "elapsedMs=([0-9]+) frames=([0-9]+)");
                        Assert(match.Success && int.Parse(match.Groups[1].Value) >= 250
                            && int.Parse(match.Groups[2].Value) >= 12, name + ": full animation with repeated DWM frames");
                        completedMonitors++;
                    }
                    Assert(completedMonitors == Screen.AllScreens.Length, name + ": waits for every monitor before activation");
                }
            }
        }
        Pump(30);
    }

    private static void Assert(bool condition, string label)
    {
        if (!condition) throw new Exception(label);
        checks++;
        output.WriteLine("PASS: " + label);
    }

    private static void RunCloseCase(bool appNeedsAttention)
    {
        string name = appNeedsAttention ? "close-confirmation" : "close-window";
        var events = new List<string>();
        using (var target = new Form
        {
            Text = "F3 close check — empty test window", BackColor = Color.FromArgb(69, 92, 116),
            StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(100, 100, 620, 410)
        })
        using (var previous = new Form
        {
            Text = "F3 close check — previous window", StartPosition = FormStartPosition.Manual,
            Bounds = new Rectangle(760, 130, 380, 220)
        })
        {
            if (appNeedsAttention) target.FormClosing += delegate(object sender, FormClosingEventArgs args) { args.Cancel = true; };
            target.Show(); previous.Show(); previous.Activate(); Pump(80);
            IntPtr handle = target.Handle;
            using (var preview = new FastPreview(delegate(string message) { events.Add(message); output.WriteLine(name + " " + message); }, true))
            {
                preview.ShowPreview(); Pump(450);
                int count = preview.WindowCount;
                preview.CloseWindow(handle);
                Pump(appNeedsAttention ? 650 : 180);
                if (appNeedsAttention)
                {
                    Assert(PreviewNative.IsWindow(handle), "close: application may cancel the close request");
                    Assert(!preview.Visible, "close: overview reveals a save prompt or other app response");
                    Assert(PreviewNative.GetForegroundWindow() == handle, "close: app receives focus for its response");
                    Assert(events.Contains("close-attention"), "close: no forced termination after close refusal");
                }
                else
                {
                    Assert(!PreviewNative.IsWindow(handle), "close: normal WM_CLOSE closes the chosen test window");
                    Assert(preview.Visible, "close: overview stays open for other windows");
                    Assert(preview.WindowCount == count - 1, "close: removed card leaves the overview immediately");
                    Assert(events.Contains("window-closed") && !events.Contains("fast-selected"), "close: closing does not select the window");
                    preview.ReleasePreview(); Pump(330);
                    Assert(!preview.Visible, "close: release F3 still returns from overview");
                }
            }
        }
        Pump(30);
    }
    private static void Pump(int duration)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < duration) { Application.DoEvents(); Thread.Sleep(3); }
    }

    private static void RunDragCases()
    {
        string title = "F3 drag verification " + Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo(Application.ExecutablePath, "--window-helper " + title.Replace(" ", "_"))
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
        };
        // A separate, empty test app verifies moving foreign-process windows.
        string helperTitle = title.Replace(" ", "_");
        using (Process helper = Process.Start(start))
        using (var desktops = new VirtualDesktops(delegate(string message) { output.WriteLine("move " + message); }))
        {
            IntPtr window = IntPtr.Zero;
            Guid original = Guid.Empty;
            try
            {
                var wait = Stopwatch.StartNew();
                while (window == IntPtr.Zero && wait.ElapsedMilliseconds < 3500)
                {
                    Pump(30);
                    window = Native.FindWindow(null, helperTitle);
                }
                Assert(window != IntPtr.Zero, "drag: foreign-process helper window exists");
                PreviewNative.ShowWindowAsync(window, 4);
                Pump(100);
                List<DesktopCard> cards = desktops.Enumerate();
                DesktopCard current = cards.Find(delegate(DesktopCard card) { return card.Current; });
                DesktopCard other = cards.Find(delegate(DesktopCard card) { return !card.Current; });
                Assert(current != null && other != null, "drag: both existing desktops are available");
                original = current.Id;
                PreviewNative.Rect before;
                PreviewNative.GetWindowRect(window, out before);
                using (var preview = new FastPreview(delegate(string message) { output.WriteLine("drag " + message); }))
                {
                    preview.ShowPreview(); Pump(450);
                    PreviewMonitor source = FindSource(preview, window);
                    Point from = Center(source.WindowImage(window));
                    Mouse(source, "OnMouseDown", from);
                    Mouse(source, "OnMouseMove", new Point(from.X + 12, from.Y + 12));
                    Assert(preview.IsDragging, "drag: movement beyond threshold starts a drag");
                    Mouse(source, "OnMouseMove", new Point(16, 16));
                    Mouse(source, "OnMouseUp", new Point(16, 16));
                    Assert(!preview.IsDragging && desktops.WindowDesktop(window) == original, "drag: invalid drop keeps the original desktop");
                    Assert(preview.Visible, "drag: invalid drop preserves the overview");

                    Mouse(source, "OnMouseDown", from);
                    Mouse(source, "OnMouseMove", new Point(from.X + 12, from.Y + 12));
                    PreviewMonitor destination = preview.PreviewMonitors.Length > 1
                        ? Array.Find(preview.PreviewMonitors, delegate(PreviewMonitor monitor) { return monitor != source; }) : source;
                    Point drop = destination.PointToScreen(Center(destination.DesktopTargetBounds(other.Id)));
                    Point dropOnSource = source.PointToClient(drop);
                    Mouse(source, "OnMouseMove", dropOnSource);
                    Assert(source.Capture && preview.IsDragging, "drag: capture tracks a target across monitors");
                    preview.ReleasePreview();
                    Assert(preview.Visible && preview.IsDragging, "drag: releasing F3 allows the pending drop to finish");
                    Mouse(source, "OnMouseUp", dropOnSource);
                    Pump(350);
                    Assert(desktops.WindowDesktop(window) == other.Id, "drag: drop moves a foreign-process window to the chosen desktop");
                    Assert(desktops.Enumerate().Find(delegate(DesktopCard card) { return card.Current; }).Id == original,
                        "drag: moving a window does not switch the current desktop");
                    Assert(!preview.Visible && !preview.IsDragging, "drag: deferred F3 release dismisses overview after drop");
                    PreviewNative.Rect after;
                    PreviewNative.GetWindowRect(window, out after);
                    Assert(before.Left == after.Left && before.Top == after.Top && before.Right == after.Right && before.Bottom == after.Bottom,
                        "drag: actual window position and size are preserved");
                }
                Assert(desktops.MoveWindow(window, original), "drag: helper can be moved back to its original desktop");
                Pump(150);
                using (var preview = new FastPreview(delegate(string message) { output.WriteLine("click " + message); }))
                {
                    preview.ShowPreview(); Pump(450);
                    PreviewMonitor source = FindSource(preview, window);
                    Point point = Center(source.WindowImage(window));
                    Mouse(source, "OnMouseDown", point);
                    Assert(!preview.IsDragging && preview.Visible, "click: button down alone does not move or select");
                    Mouse(source, "OnMouseUp", point);
                    Pump(350);
                    Assert(!preview.Visible && PreviewNative.GetForegroundWindow() == window, "click: ordinary click still selects and enlarges the window");
                    Assert(desktops.WindowDesktop(window) == original, "click: selection does not move the window to another desktop");
                }
            }
            finally
            {
                if (PreviewNative.IsWindow(window))
                {
                    if (original != Guid.Empty) desktops.MoveWindow(window, original);
                    Native.PostMessage(window, 0x10, IntPtr.Zero, IntPtr.Zero);
                    helper.WaitForExit(2000);
                }
            }
        }
    }

    private static PreviewMonitor FindSource(FastPreview preview, IntPtr window)
    {
        PreviewMonitor source = Array.Find(preview.PreviewMonitors, delegate(PreviewMonitor monitor) { return monitor.WindowImage(window) != Rectangle.Empty; });
        Assert(source != null, "drag: source thumbnail is present");
        return source;
    }
    private static Point Center(Rectangle rect) { return new Point(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2); }
    private static void Mouse(PreviewMonitor monitor, string method, Point point)
    {
        typeof(PreviewMonitor).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(monitor, new object[] { new MouseEventArgs(MouseButtons.Left, 1, point.X, point.Y, 0) });
    }
}
