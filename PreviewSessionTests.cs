using System;

internal static class PreviewSessionTests
{
    private sealed class Rig
    {
        internal IntPtr view;
        internal bool modifiers;
        internal long clock;
        internal int opens, closes;
        internal PreviewSession session;
        internal Rig()
        {
            session = new PreviewSession(delegate { return view; }, delegate { return modifiers; },
                delegate { opens++; }, delegate { closes++; }, delegate { return clock; }, delegate { });
        }
        internal void Visible() { view = new IntPtr(123); session.Tick(); }
        internal void Gone() { view = IntPtr.Zero; session.Tick(); }
    }

    private static int count;
    private static void Check(bool pass, string test)
    {
        if (!pass) throw new Exception("FAIL: " + test);
        count++;
        Console.WriteLine("PASS: " + test);
    }

    private static void Main()
    {
        var r = new Rig();
        r.session.Press(); r.Visible();
        Check(r.opens == 1 && r.closes == 0 && r.session.State == PreviewSession.Phase.Showing, "hold shows preview");
        r.session.Press(); r.session.Press();
        Check(r.opens == 1, "auto-repeat does not toggle");
        r.view = new IntPtr(456); r.session.Tick();
        Check(r.session.State == PreviewSession.Phase.Showing, "Explorer may replace the Task View HWND while holding");
        r.session.Release();
        Check(r.closes == 1, "release closes preview");
        r.Gone();
        Check(!r.session.Busy, "idle stops polling");

        r = new Rig(); r.session.Press(); r.session.Release();
        Check(r.closes == 0 && r.session.State == PreviewSession.Phase.Opening, "quick tap waits for animation");
        r.Visible();
        Check(r.closes == 1, "quick tap closes late-arriving preview");

        r = new Rig(); r.view = new IntPtr(123); r.session.Press(); r.session.Release();
        Check(r.opens == 0 && r.closes == 0 && !r.session.Busy, "pre-existing Win+Tab is preserved");

        r = new Rig(); r.session.Press(); r.Visible(); r.Gone(); r.session.Release();
        Check(r.closes == 0, "selection does not send Escape to selected app");
        r.session.Tick();
        Check(r.opens == 1, "selection while holding does not reopen");

        r = new Rig(); r.session.Press(); r.clock = 3001; r.session.Tick(); r.session.Release();
        Check(!r.session.Busy && r.closes == 0, "unavailable shell times out without Escape");

        r = new Rig(); r.session.Press(); r.Visible(); r.modifiers = true; r.session.Release();
        Check(r.closes == 0, "modifier prevents Ctrl+Esc leaking");
        r.modifiers = false; r.session.Tick();
        Check(r.closes == 1, "release closes once modifier is released");

        r = new Rig(); r.session.Press(); r.Visible(); r.session.Release(); r.session.Press();
        Check(r.opens == 1, "rapid second press waits for close animation");
        r.Gone(); r.Visible();
        Check(r.opens == 2 && r.closes == 1 && r.session.Held, "rapid second hold reopens after animation");
        r.session.Release();
        Check(r.closes == 2, "second release closes second preview");

        r = new Rig(); r.session.Press(); r.session.Release(); r.session.Press(); r.Visible();
        Check(r.opens == 1 && r.closes == 0 && r.session.Held, "re-press during opening reuses pending preview");

        r = new Rig(); r.session.Press(); r.Visible(); r.session.Release(); r.session.Tick(); r.session.Tick();
        Check(r.closes == 1, "close animation never repeats Escape");
        r.view = new IntPtr(456); r.session.Tick();
        Check(r.session.State == PreviewSession.Phase.Closing, "replaced HWND does not reopen during close animation");
        Console.WriteLine("All " + count + " checks passed.");
    }
}
