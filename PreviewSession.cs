using System;

// Independent of Win32 so timing/focus races can be tested without sending keys.
internal sealed class PreviewSession
{
    internal enum Phase { Idle, Opening, Showing, Closing }
    private readonly Func<IntPtr> foregroundView;
    private readonly Func<bool> modifiersDown;
    private readonly Action open;
    private readonly Action close;
    private readonly Func<long> now;
    private readonly Action<string> trace;
    private IntPtr ownedWindow;
    private bool borrowed;
    private long deadline;
    internal Phase State { get; private set; }
    internal bool Held { get; private set; }
    internal bool Busy { get { return State != Phase.Idle; } }

    internal PreviewSession(Func<IntPtr> foregroundView, Func<bool> modifiersDown,
        Action open, Action close, Func<long> now, Action<string> trace)
    {
        this.foregroundView = foregroundView;
        this.modifiersDown = modifiersDown;
        this.open = open;
        this.close = close;
        this.now = now;
        this.trace = trace;
    }

    internal void Press()
    {
        if (Held) return; // Ignore keyboard auto-repeat.
        Held = true;
        if (State == Phase.Idle) Begin();
        // While Closing, the next preview waits for the previous animation.
    }

    private void Begin()
    {
        ownedWindow = foregroundView();
        borrowed = ownedWindow != IntPtr.Zero;
        deadline = now() + 3000;
        State = borrowed ? Phase.Showing : Phase.Opening;
        trace(borrowed ? "existing-task-view-preserved" : "opening");
        if (!borrowed) open();
    }

    internal void Release()
    {
        Held = false;
        Tick();
    }

    internal void Tick()
    {
        if (State == Phase.Idle) return;
        IntPtr current = foregroundView();
        if (State == Phase.Closing)
        {
            if (current == IntPtr.Zero || now() >= deadline)
            {
                bool reopen = Held && current == IntPtr.Zero;
                State = Phase.Idle;
                trace("closed");
                if (reopen) Begin();
            }
            return;
        }
        if (State == Phase.Opening)
        {
            if (current == IntPtr.Zero)
            {
                if (now() >= deadline)
                {
                    State = Phase.Idle;
                    trace("open-timeout");
                }
                return;
            }
            ownedWindow = current;
            State = Phase.Showing;
            trace("visible");
        }
        if (current == IntPtr.Zero)
        {
            // A click, Esc or desktop switch already dismissed the overview.
            State = Phase.Idle;
            trace("focus-left-task-view");
            return;
        }
        // Explorer can replace the HWND while Task View is still on screen.
        ownedWindow = current;
        if (!Held)
        {
            if (borrowed)
            {
                State = Phase.Idle;
                return;
            }
            // Ctrl+Esc opens Start. Wait rather than sending a modified Esc.
            if (modifiersDown()) return;
            State = Phase.Closing;
            deadline = now() + 1000;
            trace("closing");
            close();
        }
    }
}
