using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

// Keep hidden destination windows and DWM thumbnail relationships ready before
// F3. The key path only shows prepared windows: no shell startup or enumeration.
internal sealed class FastPreview : IDisposable
{
    private readonly List<PreviewMonitor> monitors = new List<PreviewMonitor>();
    private readonly System.Windows.Forms.Timer refresh;
    private readonly Action<string> trace;
    private readonly DesktopWindows windows;
    private readonly VirtualDesktops desktops;
    private bool disposed;
    private bool requested;
    private IntPtr selectedWindow;
    private int selectionRemaining;
    private bool restoringSelection;
    private readonly System.Windows.Forms.Timer selectionRestore = new System.Windows.Forms.Timer { Interval = 15 };
    private readonly Stopwatch restoreClock = new Stopwatch();
    private sealed class ClosingWindow
    {
        internal IntPtr Window;
        internal readonly Stopwatch Clock = Stopwatch.StartNew();
    }
    private readonly List<ClosingWindow> closingWindows = new List<ClosingWindow>();
    private readonly System.Windows.Forms.Timer closeCheck = new System.Windows.Forms.Timer { Interval = 40 };
    private IntPtr draggedWindow, dragPreviousFocus;
    private DragPreviewWindow dragPreview;
    private bool releaseAfterDrag;
    internal bool Visible
    {
        get { foreach (var monitor in monitors) if (monitor.Visible) return true; return false; }
    }
    internal int WindowCount { get; private set; }
    internal bool IsDragging { get { return draggedWindow != IntPtr.Zero; } }
    internal PreviewMonitor[] PreviewMonitors { get { return monitors.ToArray(); } }
    internal int ThumbnailCount
    {
        get { int count = 0; foreach (var monitor in monitors) count += monitor.ThumbnailCount; return count; }
    }

    internal FastPreview(Action<string> trace, bool includeOwnWindowsForVerification = false)
    {
        this.trace = trace;
        windows = new DesktopWindows(includeOwnWindowsForVerification);
        desktops = new VirtualDesktops(trace);
        closeCheck.Tick += delegate { CheckClosingWindows(); };
        selectionRestore.Tick += delegate
        {
            if (selectedWindow == IntPtr.Zero) { selectionRestore.Stop(); return; }
            if (!PreviewNative.IsIconic(selectedWindow) || restoreClock.ElapsedMilliseconds >= 150)
            {
                selectionRestore.Stop();
                BeginSelectionExit();
            }
        };
        Refresh();
        trace("desktop-count=" + desktops.Enumerate().Count);
        refresh = new System.Windows.Forms.Timer { Interval = 750 };
        refresh.Tick += delegate { if (!Visible) Refresh(); };
        refresh.Start();
    }

    private void Refresh()
    {
        Screen[] screens = Screen.AllScreens;
        bool rebuild = screens.Length != monitors.Count;
        if (!rebuild)
            for (int i = 0; i < screens.Length; i++)
                if (screens[i].Bounds != monitors[i].Bounds) { rebuild = true; break; }
        if (rebuild)
        {
            foreach (var monitor in monitors) monitor.Dispose();
            monitors.Clear();
            foreach (Screen screen in screens)
            {
                var monitor = new PreviewMonitor(screen.Bounds, SelectWindow,
                    delegate { HidePreview(); }, OnAnimationFinished, SelectDesktop, CloseWindow, this);
                monitors.Add(monitor);
            }
        }
        List<WindowCard> cards = windows.Enumerate();
        List<DesktopCard> desktopCards = desktops.Enumerate();
        WindowCount = 0;
        foreach (var card in cards) if (card.OnCurrentDesktop) WindowCount++;
        foreach (PreviewMonitor monitor in monitors)
        {
            var local = new List<WindowCard>();
            var allLocal = new List<WindowCard>();
            foreach (WindowCard card in cards)
                if (Screen.FromHandle(card.Window).Bounds == monitor.Bounds)
                {
                    allLocal.Add(card);
                    if (card.OnCurrentDesktop) local.Add(card);
                }
            monitor.Prepare(local, desktopCards, allLocal);
            if (rebuild) monitor.WarmUp();
        }
    }

    internal void ShowPreview()
    {
        if (draggedWindow != IntPtr.Zero) { releaseAfterDrag = false; return; }
        if (requested || disposed || selectedWindow != IntPtr.Zero) return;
        requested = true;
        foreach (var monitor in monitors) monitor.Reveal();
        foreach (var monitor in monitors) monitor.StartMotion();
        trace("fast-visible windows=" + WindowCount + " thumbnails=" + ThumbnailCount);
    }

    internal void HidePreview(bool animate = true)
    {
        if (draggedWindow != IntPtr.Zero) { ClearDragVisuals(); RestoreDragFocus(); }
        selectionRestore.Stop();
        selectedWindow = IntPtr.Zero;
        selectionRemaining = 0;
        restoringSelection = false;
        if (!Visible && !requested) return;
        requested = false;
        foreach (var monitor in monitors) monitor.Dismiss(animate);
        trace(animate ? "fast-closing" : "fast-hidden");
    }

    private void OnAnimationFinished(string phase, long elapsed, int frames)
    {
        trace("animation-" + phase + " elapsedMs=" + elapsed + " frames=" + frames);
        if (phase == "select" && selectedWindow != IntPtr.Zero && --selectionRemaining == 0)
            CompleteSelection();
    }

    internal void ReleasePreview()
    {
        if (draggedWindow != IntPtr.Zero) { releaseAfterDrag = true; return; }
        // A click commits the choice. Releasing F3 must not interrupt its zoom.
        if (selectedWindow == IntPtr.Zero) HidePreview();
    }

    internal void SelectWindow(IntPtr window)
    {
        if (draggedWindow != IntPtr.Zero) return;
        if (selectedWindow != IntPtr.Zero || !Visible) return;
        if (!PreviewNative.IsWindow(window)) return;
        selectedWindow = window;
        requested = false;
        trace("selection-start");
        restoringSelection = PreviewNative.IsIconic(window);
        if (restoringSelection)
        {
            // Restore underneath the opaque overview, allowing DWM to supply a
            // real texture before the selected icon grows into its window.
            PreviewNative.ShowWindowAsync(window, 9);
            restoreClock.Restart();
            selectionRestore.Start();
        }
        else BeginSelectionExit();
    }

    private void BeginSelectionExit()
    {
        if (selectedWindow == IntPtr.Zero) return;
        selectionRemaining = 0;
        foreach (var monitor in monitors) if (monitor.Visible) selectionRemaining++;
        foreach (var monitor in monitors) if (monitor.Visible) monitor.SelectWindow(selectedWindow);
        if (selectionRemaining == 0) CompleteSelection();
    }

    private void CompleteSelection()
    {
        IntPtr target = selectedWindow;
        selectedWindow = IntPtr.Zero;
        restoringSelection = false;
        if (PreviewNative.IsWindow(target))
        {
            if (PreviewNative.IsIconic(target)) PreviewNative.ShowWindowAsync(target, 9);
            // Activate while the last animation frame still covers the real
            // desktop, then remove all overlays in the same UI turn.
            PreviewNative.SetForegroundWindow(target);
        }
        foreach (var monitor in monitors) monitor.Dismiss(false);
        trace("fast-selected");
    }

    private void SelectDesktop(Guid id)
    {
        HidePreview(false);
        desktops.Switch(id);
    }

    internal bool BeginWindowDrag(PreviewMonitor source, WindowCard card, Rectangle sourceImage, Point cursor)
    {
        if (!Visible || selectedWindow != IntPtr.Zero || draggedWindow != IntPtr.Zero || !PreviewNative.IsWindow(card.Window)) return false;
        draggedWindow = card.Window;
        dragPreviousFocus = PreviewNative.GetForegroundWindow();
        releaseAfterDrag = false;
        dragPreview = new DragPreviewWindow(card, sourceImage, cursor);
        foreach (var monitor in monitors) monitor.SetDragSource(card.Window);
        // Full mouse capture across monitor boundaries requires foreground ownership.
        PreviewNative.SetForegroundWindow(source.Handle);
        source.Capture = true;
        dragPreview.Show();
        UpdateWindowDrag(cursor);
        trace("drag-start");
        return true;
    }

    internal void UpdateWindowDrag(Point cursor)
    {
        if (draggedWindow == IntPtr.Zero) return;
        dragPreview.Follow(cursor);
        foreach (var monitor in monitors) monitor.SetDropHighlight(cursor);
    }

    internal bool EndWindowDrag(Point cursor)
    {
        if (draggedWindow == IntPtr.Zero) return false;
        IntPtr window = draggedWindow;
        Guid destination = Guid.Empty;
        foreach (var monitor in monitors)
        {
            Guid candidate = monitor.DesktopAt(cursor);
            if (candidate != Guid.Empty) { destination = candidate; break; }
        }
        bool hideAfter = releaseAfterDrag;
        ClearDragVisuals();
        bool moved = destination != Guid.Empty && desktops.MoveWindow(window, destination);
        RestoreDragFocus();
        if (moved) Refresh();
        trace(moved ? "drag-dropped" : "drag-cancelled");
        if (hideAfter) HidePreview();
        return moved;
    }

    internal void CancelWindowDrag()
    {
        if (draggedWindow == IntPtr.Zero) return;
        bool hideAfter = releaseAfterDrag;
        ClearDragVisuals();
        RestoreDragFocus();
        trace("drag-cancelled");
        if (hideAfter) HidePreview();
    }

    private void ClearDragVisuals()
    {
        draggedWindow = IntPtr.Zero;
        releaseAfterDrag = false;
        if (dragPreview != null) { dragPreview.Dispose(); dragPreview = null; }
        foreach (var monitor in monitors) { monitor.ResetPointerGesture(); monitor.SetDragSource(IntPtr.Zero); }
    }

    private void RestoreDragFocus()
    {
        IntPtr previous = dragPreviousFocus;
        dragPreviousFocus = IntPtr.Zero;
        if (PreviewNative.IsWindow(previous) && desktops.IsWindowCurrent(previous)) PreviewNative.SetForegroundWindow(previous);
    }

    internal void CloseWindow(IntPtr window)
    {
        if (!Visible || selectedWindow != IntPtr.Zero || !PreviewNative.IsWindow(window)) return;
        foreach (var pending in closingWindows) if (pending.Window == window) return;
        // Ask the app to close normally, retaining its own save/cancel handling.
        if (!Native.PostMessage(window, 0x10, IntPtr.Zero, IntPtr.Zero))
        {
            trace("close-request-blocked");
            return;
        }
        closingWindows.Add(new ClosingWindow { Window = window });
        closeCheck.Start();
        trace("close-requested");
    }

    private void CheckClosingWindows()
    {
        if (!Visible)
        {
            closingWindows.Clear();
            closeCheck.Stop();
            return;
        }
        for (int i = closingWindows.Count - 1; i >= 0; i--)
        {
            ClosingWindow pending = closingWindows[i];
            if (!PreviewNative.IsWindow(pending.Window) || !PreviewNative.IsWindowVisible(pending.Window))
            {
                foreach (var monitor in monitors) monitor.RemoveWindow(pending.Window);
                WindowCount = Math.Max(0, WindowCount - 1);
                closingWindows.RemoveAt(i);
                trace("window-closed");
                continue;
            }
            IntPtr popup = PreviewNative.GetLastActivePopup(pending.Window);
            bool modal = popup != pending.Window && PreviewNative.IsWindowVisible(popup)
                && !PreviewNative.IsWindowEnabled(pending.Window);
            if (selectedWindow == IntPtr.Zero && (modal || pending.Clock.ElapsedMilliseconds >= 450))
            {
                IntPtr attention = modal ? popup : pending.Window;
                HidePreview(false);
                if (PreviewNative.IsIconic(attention)) PreviewNative.ShowWindowAsync(attention, 9);
                PreviewNative.SetForegroundWindow(attention);
                closingWindows.Clear();
                trace("close-attention");
                break;
            }
        }
        if (closingWindows.Count == 0) closeCheck.Stop();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (draggedWindow != IntPtr.Zero) { ClearDragVisuals(); RestoreDragFocus(); }
        selectionRestore.Dispose();
        closeCheck.Dispose();
        refresh.Dispose();
        foreach (var monitor in monitors) monitor.Dispose();
        monitors.Clear();
        windows.Dispose();
        desktops.Dispose();
    }
}

internal sealed class WindowCard
{
    internal IntPtr Window;
    internal string Title;
    internal bool Minimized;
    internal Size SourceSize;
    internal Rectangle OriginalBounds;
    internal Icon AppIcon;
    internal Guid DesktopId;
    internal bool OnCurrentDesktop;
}

internal sealed class PreviewMonitor : Form
{
    internal static bool Inspectable;
    private sealed class Tile
    {
        internal WindowCard Card;
        internal Rectangle Bounds;
        internal Rectangle Image;
        internal IntPtr Thumbnail;
        internal Rectangle Origin;
    }
    private readonly List<Tile> tiles = new List<Tile>();
    private sealed class DesktopTile
    {
        internal DesktopCard Card;
        internal Rectangle Bounds, Image;
        internal readonly Dictionary<IntPtr, IntPtr> Thumbnails = new Dictionary<IntPtr, IntPtr>();
        internal readonly List<WindowCard> Windows = new List<WindowCard>();
    }
    private readonly List<DesktopTile> desktopTiles = new List<DesktopTile>();
    private readonly Action<Guid> selectDesktop;
    private readonly Action<IntPtr> closeWindow;
    private string miniSignature;
    private bool miniVisible;
    private DesktopTile hoveredDesktop;
    private readonly Action<IntPtr> select;
    private readonly Action cancel;
    private readonly Font headingFont = new Font("Segoe UI", 10, FontStyle.Regular);
    private readonly Font titleFont = new Font("Segoe UI", 9, FontStyle.Regular);
    private readonly Font hintFont = new Font("Segoe UI", 9, FontStyle.Regular);
    private readonly Bitmap background;
    private Bitmap chrome;
    private readonly AnimationPulse animation;
    private const int FrameMessage = 0x8055;
    private bool animating;
    private readonly Stopwatch animationClock = new Stopwatch();
    private readonly Action<string, long, int> animationFinished;
    private double progress;
    private double animationFrom;
    private bool opening;
    private bool selecting;
    private Tile selectedTile;
    private Rectangle selectionFrom, selectionTo;
    private double selectionProgress;
    private double animationDuration;
    private int frames;
    private Tile hovered;
    private bool closeHovered;
    private readonly FastPreview owner;
    private Tile pressedTile;
    private Point pressPoint;
    private bool dragging;
    private IntPtr dragSource;
    private DesktopTile dropHighlight;
    internal int ThumbnailCount
    {
        get { int count = 0; foreach (var tile in tiles) if (tile.Thumbnail != IntPtr.Zero) count++; return count; }
    }

    internal PreviewMonitor(Rectangle screen, Action<IntPtr> select, Action cancel,
        Action<string, long, int> animationFinished, Action<Guid> selectDesktop, Action<IntPtr> closeWindow, FastPreview owner)
    {
        this.select = select;
        this.cancel = cancel;
        this.animationFinished = animationFinished;
        this.selectDesktop = selectDesktop;
        this.closeWindow = closeWindow;
        this.owner = owner;
        Text = "F3 Window Peek — szybki podgląd";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = Inspectable;
        StartPosition = FormStartPosition.Manual;
        Bounds = screen;
        BackColor = Color.FromArgb(18, 22, 30);
        DoubleBuffered = true;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.None;
        Cursor = Cursors.Default;
        background = CreateBackground(screen.Size);
        animation = new AnimationPulse(Handle, FrameMessage);
    }

    private void AnimateFrame()
    {
            if (!animating) return;
            double t = Math.Min(1, animationClock.Elapsed.TotalMilliseconds / animationDuration);
            double ease = PreviewLayout.EaseOut(t);
            progress = opening ? animationFrom + (1 - animationFrom) * ease : animationFrom * (1 - ease);
            selectionProgress = ease;
            SetThumbnailFrame();
            frames++;
            if (t >= 1)
            {
                StopMotion();
                if (!opening && !selecting) Hide();
                else Invalidate();
                animationFinished(selecting ? "select" : opening ? "open" : "close", animationClock.ElapsedMilliseconds, frames);
            }
    }
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            if (!Inspectable) parameters.ExStyle |= 0x80 | 0x08000000; // Tool window, no activation.
            return parameters;
        }
    }
    protected override void WndProc(ref Message message)
    {
        if (message.Msg == FrameMessage && animation != null)
        {
            animation.Acknowledge();
            AnimateFrame();
            return;
        }
        if (message.Msg == 0x21) { message.Result = new IntPtr(3); return; } // MA_NOACTIVATE
        base.WndProc(ref message);
    }

    internal void WarmUp()
    {
        // Exercise first-show WinForms/JIT work once, entirely offscreen.
        Rectangle target = Bounds;
        Bounds = new Rectangle(-32000, -32000, target.Width, target.Height);
        Show();
        Update();
        Hide();
        Bounds = target;
    }

    internal void Prepare(List<WindowCard> cards, List<DesktopCard> desktopCards, List<WindowCard> allCards)
    {
        var signature = new StringBuilder();
        foreach (var desktop in desktopCards) signature.Append(desktop.Id).Append(desktop.Name).Append(desktop.Current);
        foreach (var card in allCards) signature.Append(card.Window).Append(card.DesktopId).Append(card.OriginalBounds).Append(card.Title).Append(card.Minimized);
        bool desktopChanged = signature.ToString() != miniSignature;
        bool changed = cards.Count != tiles.Count;
        if (!changed)
            for (int i = 0; i < cards.Count; i++)
            {
                WindowCard old = tiles[i].Card;
                WindowCard next = cards[i];
                if (old.Window != next.Window || old.Title != next.Title || old.SourceSize != next.SourceSize
                    || old.Minimized != next.Minimized || old.OriginalBounds != next.OriginalBounds) { changed = true; break; }
            }
        if (!changed && !desktopChanged) return;
        var stableBounds = new Dictionary<IntPtr, Rectangle>();
        bool preserve = Visible && !animating;
        foreach (var card in cards)
        {
            Tile old = tiles.Find(delegate(Tile tile) { return tile.Card.Window == card.Window; });
            if (old == null) { preserve = false; break; }
            stableBounds[card.Window] = old.Bounds;
        }
        miniSignature = signature.ToString();
        ClearTiles();
        PrepareDesktops(desktopCards, allCards);
        Size gridSize = new Size(ClientSize.Width, ClientSize.Height - (desktopTiles.Count > 0 ? 170 : 0));
        Rectangle[] layout = PreviewLayout.Arrange(cards.Count, gridSize);
        if (preserve) for (int i = 0; i < cards.Count; i++) layout[i] = stableBounds[cards[i].Window];
        for (int i = 0; i < cards.Count; i++)
        {
            var tile = new Tile { Card = cards[i], Bounds = layout[i] };
            var imageArea = new Rectangle(tile.Bounds.X + 10, tile.Bounds.Y + 10,
                Math.Max(1, tile.Bounds.Width - 20), Math.Max(1, tile.Bounds.Height - 52));
            tile.Image = PreviewLayout.Fit(cards[i].SourceSize, imageArea);
            Rectangle origin = cards[i].OriginalBounds;
            origin.Offset(-Bounds.Left, -Bounds.Top);
            if (cards[i].Minimized || origin.Width <= 1 || origin.Height <= 1)
                origin = new Rectangle(ClientSize.Width / 2 - 16, ClientSize.Height - 24, 32, 20);
            tile.Origin = origin;
            tiles.Add(tile);
        }
        // Preserve window stacking while their initially overlapping images move.
        for (int i = tiles.Count - 1; i >= 0; i--)
        {
            Tile tile = tiles[i];
            if (tile.Card.Minimized) continue;
            IntPtr thumbnail;
            if (PreviewNative.DwmRegisterThumbnail(Handle, tile.Card.Window, out thumbnail) >= 0)
            {
                tile.Thumbnail = thumbnail;
                PreviewNative.Size actual;
                var imageArea = new Rectangle(tile.Bounds.X + 10, tile.Bounds.Y + 10,
                    Math.Max(1, tile.Bounds.Width - 20), Math.Max(1, tile.Bounds.Height - 52));
                if (PreviewNative.DwmQueryThumbnailSourceSize(thumbnail, out actual) >= 0 && actual.Width > 0 && actual.Height > 0)
                    tile.Image = PreviewLayout.Fit(new Size(actual.Width, actual.Height), imageArea);
                var properties = new PreviewNative.ThumbnailProperties
                {
                    Flags = 1 | 4 | 8 | 16,
                    Destination = new PreviewNative.Rect(tile.Image),
                    Opacity = 255,
                    Visible = true,
                    SourceClientOnly = false
                };
                if (PreviewNative.DwmUpdateThumbnailProperties(thumbnail, ref properties) < 0)
                {
                    PreviewNative.DwmUnregisterThumbnail(thumbnail);
                    tile.Thumbnail = IntPtr.Zero;
                }
            }
        }
        BuildChrome();
        ShowDesktopThumbnails(Visible && progress > 0.9999 && !animating);
        Invalidate();
    }

    internal void Reveal()
    {
        hovered = null;
        if (!Visible) progress = 0;
        PrepareMotion(true);
        SetThumbnailFrame();
        Show();
    }

    internal void Dismiss(bool animate)
    {
        ResetPointerGesture();
        if (!Visible) return;
        if (!animate) { StopMotion(); selecting = false; selectedTile = null; progress = 0; Hide(); return; }
        PrepareMotion(false);
        // Remove stationary labels once; do not repaint the screen per frame.
        Invalidate();
        StartMotion();
    }

    private void PrepareMotion(bool show)
    {
        StopMotion();
        selecting = false;
        selectedTile = null;
        opening = show;
        animationFrom = progress;
        animationDuration = show ? 345 : 255;
        frames = 0;
        ShowDesktopThumbnails(false);
    }

    internal void SelectWindow(IntPtr window)
    {
        Tile chosen = tiles.Find(delegate(Tile tile) { return tile.Card.Window == window; });
        Rectangle from = chosen == null ? Rectangle.Empty : PreviewLayout.Interpolate(chosen.Origin, chosen.Image, progress);
        PrepareMotion(false);
        selecting = true;
        selectedTile = chosen;
        selectionProgress = 0;
        if (chosen != null)
        {
            selectionFrom = from;
            PreviewNative.Rect realBounds;
            selectionTo = chosen.Origin;
            if (PreviewNative.GetWindowRect(window, out realBounds) && realBounds.Right - realBounds.Left > 1
                && realBounds.Bottom - realBounds.Top > 1 && realBounds.Left > -30000)
                selectionTo = Rectangle.FromLTRB(realBounds.Left - Bounds.Left, realBounds.Top - Bounds.Top,
                    realBounds.Right - Bounds.Left, realBounds.Bottom - Bounds.Top);
            // Last registered thumbnail is above the others while it expands.
            IntPtr top;
            if (PreviewNative.DwmRegisterThumbnail(Handle, window, out top) >= 0)
            {
                var properties = new PreviewNative.ThumbnailProperties
                {
                    Flags = 1 | 4 | 8 | 16, Destination = new PreviewNative.Rect(selectionFrom),
                    Opacity = 255, Visible = true, SourceClientOnly = false
                };
                if (PreviewNative.DwmUpdateThumbnailProperties(top, ref properties) >= 0)
                {
                    if (chosen.Thumbnail != IntPtr.Zero) PreviewNative.DwmUnregisterThumbnail(chosen.Thumbnail);
                    chosen.Thumbnail = top;
                }
                else PreviewNative.DwmUnregisterThumbnail(top);
            }
        }
        SetThumbnailFrame();
        Invalidate();
        StartMotion();
    }

    internal void StartMotion()
    {
        animating = true;
        animationClock.Restart();
        animation.Start();
    }

    private void StopMotion()
    {
        animating = false;
        if (animation != null) animation.Stop();
    }

    private void SetThumbnailFrame()
    {
        foreach (var tile in tiles)
        {
            if (tile.Thumbnail == IntPtr.Zero) continue;
            if (tile.Card.Window == dragSource) continue;
            Rectangle rect = PreviewLayout.Interpolate(tile.Origin, tile.Image, progress);
            if (selecting && tile == selectedTile)
                rect = PreviewLayout.Interpolate(selectionFrom, selectionTo, selectionProgress);
            var properties = new PreviewNative.ThumbnailProperties
            {
                Flags = 1 | 4 | 8,
                Destination = new PreviewNative.Rect(rect),
                Opacity = tile.Card.Minimized && tile != selectedTile ? (byte)(255 * progress) : (byte)255,
                Visible = true
            };
            PreviewNative.DwmUpdateThumbnailProperties(tile.Thumbnail, ref properties);
        }
        ShowDesktopThumbnails(progress > 0.9999 && opening && !selecting);
    }

    private void PrepareDesktops(List<DesktopCard> cards, List<WindowCard> allCards)
    {
        if (cards.Count == 0) return;
        int gap = 18;
        int width = Math.Max(32, Math.Min(210, (ClientSize.Width - 96 - (cards.Count - 1) * gap) / cards.Count));
        int height = Math.Max(20, Math.Min(118, width * ClientSize.Height / Math.Max(1, ClientSize.Width)));
        width = Math.Max(20, Math.Min(width, height * ClientSize.Width / Math.Max(1, ClientSize.Height)));
        int left = (ClientSize.Width - cards.Count * width - (cards.Count - 1) * gap) / 2;
        int top = ClientSize.Height - 205;
        for (int i = 0; i < cards.Count; i++)
        {
            var tile = new DesktopTile { Card = cards[i], Image = new Rectangle(left + i * (width + gap), top, width, height),
                Bounds = new Rectangle(left + i * (width + gap) - 5, top - 5, width + 10, height + 38) };
            foreach (var card in allCards)
                if (card.DesktopId == tile.Card.Id || IsPinned(card.DesktopId)) tile.Windows.Add(card);
            for (int j = tile.Windows.Count - 1; j >= 0; j--)
            {
                WindowCard card = tile.Windows[j];
                if (card.Minimized) continue;
                Rectangle dest = MiniWindowBounds(card, tile.Image);
                if (dest.Width <= 1 || dest.Height <= 1) continue;
                IntPtr thumbnail;
                if (PreviewNative.DwmRegisterThumbnail(Handle, card.Window, out thumbnail) < 0) continue;
                var properties = new PreviewNative.ThumbnailProperties
                {
                    Flags = 1 | 4 | 8 | 16,
                    Destination = new PreviewNative.Rect(dest), Opacity = 255, Visible = false, SourceClientOnly = false
                };
                if (PreviewNative.DwmUpdateThumbnailProperties(thumbnail, ref properties) >= 0) tile.Thumbnails.Add(card.Window, thumbnail);
                else PreviewNative.DwmUnregisterThumbnail(thumbnail);
            }
            desktopTiles.Add(tile);
        }
    }

    private static bool IsPinned(Guid id)
    {
        return id == new Guid("BB64D5B7-4DE3-4AB2-A87C-DB7601AEA7DC") || id == new Guid("C2DDEA68-66F2-4CF9-8264-1BFD00FBBBAC");
    }

    private Rectangle MiniWindowBounds(WindowCard card, Rectangle image)
    {
        Rectangle window = card.OriginalBounds;
        var rect = new Rectangle(image.X + (window.X - Bounds.X) * image.Width / Math.Max(1, Bounds.Width),
            image.Y + (window.Y - Bounds.Y) * image.Height / Math.Max(1, Bounds.Height),
            Math.Max(1, window.Width * image.Width / Math.Max(1, Bounds.Width)),
            Math.Max(1, window.Height * image.Height / Math.Max(1, Bounds.Height)));
        return Rectangle.Intersect(image, rect);
    }

    private void ShowDesktopThumbnails(bool visible)
    {
        if (miniVisible == visible) return;
        miniVisible = visible;
        var properties = new PreviewNative.ThumbnailProperties { Flags = 8, Visible = visible };
        foreach (var desktop in desktopTiles)
            foreach (IntPtr thumbnail in desktop.Thumbnails.Values) PreviewNative.DwmUpdateThumbnailProperties(thumbnail, ref properties);
    }

    private void BuildChrome()
    {
        if (chrome != null) chrome.Dispose();
        chrome = new Bitmap(Math.Max(1, ClientSize.Width), Math.Max(1, ClientSize.Height));
        using (Graphics graphics = Graphics.FromImage(chrome)) RenderChrome(graphics);
    }

    private void RenderChrome(Graphics graphics)
    {
        graphics.DrawImageUnscaled(background, 0, 0);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        string heading = "Wszystkie okna  ·  " + tiles.Count;
        int headingWidth = TextRenderer.MeasureText(heading, headingFont).Width + 34;
        var headingBounds = new Rectangle((ClientSize.Width - headingWidth) / 2, 26, headingWidth, 34);
        using (var fill = new SolidBrush(Color.FromArgb(115, 16, 22, 32)))
        using (GraphicsPath shape = Rounded(headingBounds, 16)) graphics.FillPath(fill, shape);
        TextRenderer.DrawText(graphics, heading, headingFont, headingBounds, Color.FromArgb(240, 244, 250),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        string hint = "Puść F3, aby wrócić     ·     Kliknij okno, aby je wybrać     ·     Esc";
        var hintBounds = new Rectangle(0, ClientSize.Height - 44, ClientSize.Width, 22);
        TextRenderer.DrawText(graphics, hint, hintFont, hintBounds, Color.FromArgb(215, 222, 234),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        if (tiles.Count == 0)
            TextRenderer.DrawText(graphics, "Brak okien na tym ekranie", titleFont, ClientRectangle,
                Color.FromArgb(153, 165, 185), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        foreach (var tile in tiles)
        {
            if (tile.Card.Minimized)
            {
                using (var fill = new SolidBrush(Color.FromArgb(140, 24, 33, 49))) graphics.FillRectangle(fill, tile.Image);
                int cx = tile.Image.Left + tile.Image.Width / 2;
                int cy = tile.Image.Top + tile.Image.Height / 2;
                if (tile.Card.AppIcon != null) graphics.DrawIcon(tile.Card.AppIcon, new Rectangle(cx - 28, cy - 38, 56, 56));
                var minimizedLabel = new Rectangle(tile.Image.Left, cy + 28, tile.Image.Width, 26);
                TextRenderer.DrawText(graphics, "Zminimalizowane", hintFont, minimizedLabel, Color.FromArgb(205, 215, 231),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            for (int spread = 14; spread >= 2; spread -= 2)
            {
                Rectangle shadow = Rectangle.Inflate(tile.Image, spread, spread);
                shadow.Offset(0, 5);
                using (var pen = new Pen(Color.FromArgb(12, 0, 0, 0), 3))
                using (GraphicsPath shape = Rounded(shadow, 7)) graphics.DrawPath(pen, shape);
            }
            using (var pen = new Pen(Color.FromArgb(85, 224, 232, 247), 1)) graphics.DrawRectangle(pen, tile.Image);
            int labelWidth = Math.Min(tile.Bounds.Width - 8, TextRenderer.MeasureText(tile.Card.Title, titleFont).Width + 28);
            Rectangle label = new Rectangle(tile.Bounds.X + (tile.Bounds.Width - labelWidth) / 2,
                Math.Min(tile.Image.Bottom + 13, tile.Bounds.Bottom - 31), Math.Max(1, labelWidth), 27);
            using (var fill = new SolidBrush(Color.FromArgb(175, 17, 23, 34)))
            using (GraphicsPath shape = Rounded(label, 10)) graphics.FillPath(fill, shape);
            Rectangle textBounds = Rectangle.Inflate(label, -10, 0);
            TextRenderer.DrawText(graphics, tile.Card.Title, titleFont, textBounds,
                Color.FromArgb(242, 245, 250), TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine
                | TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
            if (tile.Thumbnail == IntPtr.Zero && !tile.Card.Minimized)
                TextRenderer.DrawText(graphics, tile.Card.Minimized ? "Zminimalizowane okno" : "Podgląd niedostępny",
                    hintFont, tile.Image, Color.FromArgb(153, 165, 185), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        DrawDesktopStrip(graphics);
    }

    private void DrawDesktopStrip(Graphics graphics)
    {
        if (desktopTiles.Count == 0) return;
        Rectangle dock = Rectangle.Union(desktopTiles[0].Bounds, desktopTiles[desktopTiles.Count - 1].Bounds);
        dock.Inflate(18, 12);
        using (var fill = new SolidBrush(Color.FromArgb(150, 15, 21, 32)))
        using (GraphicsPath shape = Rounded(dock, 17)) graphics.FillPath(fill, shape);
        foreach (var desktop in desktopTiles)
        {
            graphics.DrawImage(background, desktop.Image);
            foreach (var window in desktop.Windows)
            {
                if (window.Minimized) continue;
                Rectangle rect = MiniWindowBounds(window, desktop.Image);
                if (rect.Width < 2 || rect.Height < 2) continue;
                using (var fill = new SolidBrush(Color.FromArgb(220, 42, 49, 65))) graphics.FillRectangle(fill, rect);
                using (var pen = new Pen(Color.FromArgb(170, 117, 133, 156), 1)) graphics.DrawRectangle(pen, rect);
            }
            Rectangle border = Rectangle.Inflate(desktop.Image, 3, 3);
            using (var pen = new Pen(desktop.Card.Current ? Color.FromArgb(147, 197, 255) : Color.FromArgb(105, 130, 151, 180), desktop.Card.Current ? 2 : 1))
                graphics.DrawRectangle(pen, border);
            Rectangle label = new Rectangle(desktop.Bounds.X, desktop.Image.Bottom + 9, desktop.Bounds.Width, 23);
            TextRenderer.DrawText(graphics, desktop.Card.Name, titleFont, label, Color.FromArgb(238, 243, 250),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (chrome != null && progress >= 0.999 && !animating) e.Graphics.DrawImageUnscaled(chrome, 0, 0);
        else e.Graphics.DrawImageUnscaled(background, 0, 0);
        if (hovered != null && progress > 0.99)
        {
            Rectangle outline = Rectangle.Inflate(hovered.Image, 3, 3);
            using (var pen = new Pen(Color.FromArgb(151, 200, 255), 2)) e.Graphics.DrawRectangle(pen, outline);
            if (!animating)
            {
                Rectangle button = CloseBounds(hovered);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var fill = new SolidBrush(closeHovered ? Color.FromArgb(211, 65, 73) : Color.FromArgb(47, 57, 73)))
                    e.Graphics.FillEllipse(fill, button);
                using (var border = new Pen(Color.FromArgb(130, 215, 225, 242))) e.Graphics.DrawEllipse(border, button);
                using (var cross = new Pen(Color.FromArgb(246, 248, 252), 1.7f))
                {
                    int inset = 8;
                    e.Graphics.DrawLine(cross, button.Left + inset, button.Top + inset, button.Right - inset, button.Bottom - inset);
                    e.Graphics.DrawLine(cross, button.Right - inset, button.Top + inset, button.Left + inset, button.Bottom - inset);
                }
            }
        }
        if (hoveredDesktop != null && progress > 0.99)
            using (var pen = new Pen(Color.FromArgb(190, 222, 255), 2))
                e.Graphics.DrawRectangle(pen, Rectangle.Inflate(hoveredDesktop.Image, 4, 4));
        if (dropHighlight != null)
            using (var pen = new Pen(Color.FromArgb(174, 218, 255), 4))
                e.Graphics.DrawRectangle(pen, Rectangle.Inflate(dropHighlight.Image, 6, 6));
    }

    private static GraphicsPath Rounded(Rectangle bounds, int radius)
    {
        int diameter = Math.Max(1, Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height)));
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static Bitmap CreateBackground(Size size)
    {
        var result = new Bitmap(Math.Max(1, size.Width), Math.Max(1, size.Height));
        using (Graphics graphics = Graphics.FromImage(result))
        {
            using (var gradient = new LinearGradientBrush(new Rectangle(Point.Empty, size),
                Color.FromArgb(64, 78, 100), Color.FromArgb(25, 33, 49), 35))
                graphics.FillRectangle(gradient, new Rectangle(Point.Empty, size));
            var path = new StringBuilder(1024);
            if (PreviewNative.SystemParametersInfo(0x73, path.Capacity, path, 0) && File.Exists(path.ToString()))
            {
                try
                {
                    using (Image wallpaper = Image.FromFile(path.ToString()))
                    using (var small = new Bitmap(96, Math.Max(1, 96 * size.Height / Math.Max(1, size.Width))))
                    {
                        using (Graphics downsample = Graphics.FromImage(small))
                        {
                            downsample.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            double fill = Math.Max((double)small.Width / wallpaper.Width, (double)small.Height / wallpaper.Height);
                            int width = (int)(wallpaper.Width * fill), height = (int)(wallpaper.Height * fill);
                            downsample.DrawImage(wallpaper, new Rectangle((small.Width - width) / 2, (small.Height - height) / 2, width, height));
                        }
                        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        graphics.DrawImage(small, new Rectangle(Point.Empty, size));
                    }
                }
                catch (ArgumentException) { }
                catch (IOException) { }
                catch (OutOfMemoryException) { }
            }
            using (var dim = new SolidBrush(Color.FromArgb(95, 10, 16, 27)))
                graphics.FillRectangle(dim, new Rectangle(Point.Empty, size));
        }
        return result;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (pressedTile != null)
        {
            Size threshold = SystemInformation.DragSize;
            Rectangle clickArea = new Rectangle(pressPoint.X - threshold.Width / 2, pressPoint.Y - threshold.Height / 2, threshold.Width, threshold.Height);
            if (!dragging && !clickArea.Contains(e.Location))
            {
                if (animating || !owner.BeginWindowDrag(this, pressedTile.Card, RectangleToScreen(pressedTile.Image), PointToScreen(e.Location)))
                {
                    ResetPointerGesture();
                    return;
                }
                dragging = true;
            }
            if (dragging) { owner.UpdateWindowDrag(PointToScreen(e.Location)); return; }
        }
        Tile next = null;
        if (hovered != null && CloseBounds(hovered).Contains(e.Location)) next = hovered;
        else foreach (var tile in tiles) if (tile.Bounds.Contains(e.Location)) { next = tile; break; }
        bool nextClose = next != null && CloseBounds(next).Contains(e.Location);
        DesktopTile nextDesktop = null;
        foreach (var desktop in desktopTiles) if (desktop.Bounds.Contains(e.Location)) { nextDesktop = desktop; break; }
        if (next == hovered && nextDesktop == hoveredDesktop && nextClose == closeHovered) return;
        hovered = next;
        closeHovered = nextClose;
        hoveredDesktop = nextDesktop;
        Cursor = hovered == null && hoveredDesktop == null ? Cursors.Default : Cursors.Hand;
        Invalidate();
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (selecting) return;
        if (e.Button != MouseButtons.Left) return;
        if (!animating && hovered != null && CloseBounds(hovered).Contains(e.Location))
        {
            closeWindow(hovered.Card.Window);
            return;
        }
        foreach (var desktop in desktopTiles)
            if (desktop.Bounds.Contains(e.Location)) { selectDesktop(desktop.Card.Id); return; }
        foreach (var tile in tiles)
            if (tile.Bounds.Contains(e.Location))
            {
                pressedTile = tile;
                pressPoint = e.Location;
                Capture = true;
                return;
            }
        cancel();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left || pressedTile == null) return;
        Tile chosen = pressedTile;
        bool wasDragging = dragging;
        ResetPointerGesture();
        if (wasDragging) owner.EndWindowDrag(PointToScreen(e.Location));
        else if (chosen.Bounds.Contains(e.Location)) select(chosen.Card.Window);
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture && pressedTile != null)
        {
            bool wasDragging = dragging;
            pressedTile = null;
            dragging = false;
            if (wasDragging) owner.CancelWindowDrag();
        }
    }

    internal void ResetPointerGesture()
    {
        pressedTile = null;
        dragging = false;
        Capture = false;
    }

    internal void SetDragSource(IntPtr window)
    {
        dragSource = window;
        dropHighlight = null;
        foreach (var tile in tiles)
        {
            if (tile.Thumbnail == IntPtr.Zero) continue;
            var properties = new PreviewNative.ThumbnailProperties { Flags = 8, Visible = tile.Card.Window != window };
            PreviewNative.DwmUpdateThumbnailProperties(tile.Thumbnail, ref properties);
        }
        Cursor = window == IntPtr.Zero ? Cursors.Default : Cursors.SizeAll;
        Invalidate();
    }

    internal Guid DesktopAt(Point screenPoint)
    {
        if (!Visible) return Guid.Empty;
        Point point = PointToClient(screenPoint);
        foreach (var desktop in desktopTiles) if (desktop.Bounds.Contains(point)) return desktop.Card.Id;
        return Guid.Empty;
    }

    internal Rectangle WindowImage(IntPtr window)
    {
        Tile tile = tiles.Find(delegate(Tile item) { return item.Card.Window == window; });
        return tile == null ? Rectangle.Empty : tile.Image;
    }

    internal Rectangle DesktopTargetBounds(Guid id)
    {
        DesktopTile tile = desktopTiles.Find(delegate(DesktopTile item) { return item.Card.Id == id; });
        return tile == null ? Rectangle.Empty : tile.Bounds;
    }

    internal void SetDropHighlight(Point screenPoint)
    {
        Guid id = DesktopAt(screenPoint);
        DesktopTile next = desktopTiles.Find(delegate(DesktopTile desktop) { return desktop.Card.Id == id; });
        if (next == dropHighlight) return;
        dropHighlight = next;
        Invalidate();
    }

    private static Rectangle CloseBounds(Tile tile)
    {
        // Keep the control above the image: DWM thumbnails cover GDI content
        // drawn inside their destination rectangle.
        return new Rectangle(tile.Image.Right - 26, tile.Image.Top - 32, 26, 26);
    }

    internal void RemoveWindow(IntPtr window)
    {
        Tile tile = tiles.Find(delegate(Tile item) { return item.Card.Window == window; });
        if (tile != null)
        {
            if (tile.Thumbnail != IntPtr.Zero) PreviewNative.DwmUnregisterThumbnail(tile.Thumbnail);
            tiles.Remove(tile);
            if (hovered == tile) { hovered = null; closeHovered = false; }
        }
        foreach (var desktop in desktopTiles)
        {
            IntPtr thumbnail;
            if (desktop.Thumbnails.TryGetValue(window, out thumbnail))
            {
                PreviewNative.DwmUnregisterThumbnail(thumbnail);
                desktop.Thumbnails.Remove(window);
            }
            desktop.Windows.RemoveAll(delegate(WindowCard card) { return card.Window == window; });
        }
        // Keep the other cards in place so repeated close clicks have stable targets.
        BuildChrome();
        Invalidate();
    }

    private void ClearTiles()
    {
        foreach (var desktop in desktopTiles)
            foreach (IntPtr thumbnail in desktop.Thumbnails.Values) PreviewNative.DwmUnregisterThumbnail(thumbnail);
        desktopTiles.Clear();
        dropHighlight = null;
        hoveredDesktop = null;
        miniVisible = false;
        hovered = null;
        closeHovered = false;
        foreach (var tile in tiles)
            if (tile.Thumbnail != IntPtr.Zero) PreviewNative.DwmUnregisterThumbnail(tile.Thumbnail);
        tiles.Clear();
    }
    protected override void Dispose(bool disposing)
    {
        ClearTiles();
        if (disposing)
        {
            StopMotion();
            headingFont.Dispose(); titleFont.Dispose(); hintFont.Dispose();
            animation.Dispose();
            background.Dispose();
            if (chrome != null) chrome.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal sealed class DesktopWindows : IDisposable
{
    private readonly uint ownPid = (uint)Process.GetCurrentProcess().Id;
    private readonly IVirtualDesktopManager desktop;
    private readonly Dictionary<uint, Icon> icons = new Dictionary<uint, Icon>();
    private readonly bool includeOwn;
    internal DesktopWindows(bool includeOwn = false)
    {
        this.includeOwn = includeOwn;
        try { desktop = (IVirtualDesktopManager)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("AA509086-5CA9-4C25-8F95-589D3C07B48A"))); }
        catch (COMException) { }
    }
    internal List<WindowCard> Enumerate()
    {
        var cards = new List<WindowCard>();
        PreviewNative.EnumWindows(delegate(IntPtr window, IntPtr unused)
        {
            if (!PreviewNative.IsWindowVisible(window)) return true;
            uint pid;
            PreviewNative.GetWindowThreadProcessId(window, out pid);
            if (pid == ownPid && !includeOwn) return true;
            long style = PreviewNative.GetWindowLongPtr(window, -20).ToInt64();
            if ((style & 0x80) != 0 || (style & 0x08000000) != 0) return true;
            if (PreviewNative.GetWindow(window, 4) != IntPtr.Zero && (style & 0x40000) == 0) return true;
            int cloaked;
            PreviewNative.DwmGetWindowAttribute(window, 14, out cloaked, 4);
            bool onCurrent = true;
            Guid desktopId = Guid.Empty;
            if (desktop != null)
            {
                desktop.IsWindowOnCurrentVirtualDesktop(window, out onCurrent);
                desktop.GetWindowDesktopId(window, out desktopId);
            }
            if (cloaked != 0 && (onCurrent || desktopId == Guid.Empty)) return true;
            var title = new StringBuilder(512);
            PreviewNative.GetWindowText(window, title, title.Capacity);
            if (title.Length == 0) return true;
            var className = new StringBuilder(128);
            PreviewNative.GetClassName(window, className, className.Capacity);
            string cls = className.ToString();
            if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd"
                || cls == "XamlExplorerHostIslandWindow" || cls == "MultitaskingViewFrame") return true;
            PreviewNative.Rect bounds;
            PreviewNative.GetWindowRect(window, out bounds);
            int width = Math.Max(1, bounds.Right - bounds.Left);
            int height = Math.Max(1, bounds.Bottom - bounds.Top);
            // Minimized windows have offscreen/icon-sized bounds. DWM knows the original size.
            if (PreviewNative.IsIconic(window)) { width = 1280; height = 800; }
            bool minimized = PreviewNative.IsIconic(window);
            cards.Add(new WindowCard { Window = window, Title = title.ToString(),
                Minimized = minimized, SourceSize = new Size(width, height), AppIcon = minimized ? GetIcon(pid) : null,
                OriginalBounds = Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom),
                DesktopId = desktopId, OnCurrentDesktop = onCurrent });
            return true;
        }, IntPtr.Zero);
        return cards;
    }
    private Icon GetIcon(uint pid)
    {
        Icon icon;
        if (icons.TryGetValue(pid, out icon)) return icon;
        try
        {
            using (var process = Process.GetProcessById((int)pid))
                icon = Icon.ExtractAssociatedIcon(process.MainModule.FileName);
        }
        catch (ArgumentException) { icon = null; }
        catch (InvalidOperationException) { icon = null; }
        catch (System.ComponentModel.Win32Exception) { icon = null; }
        if (icon == null) icon = (Icon)SystemIcons.Application.Clone();
        icons[pid] = icon;
        return icon;
    }
    public void Dispose()
    {
        if (desktop != null) Marshal.FinalReleaseComObject(desktop);
        foreach (var icon in icons.Values) icon.Dispose();
        icons.Clear();
    }

    [ComImport, Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopManager
    {
        [PreserveSig] int IsWindowOnCurrentVirtualDesktop(IntPtr window, [MarshalAs(UnmanagedType.Bool)] out bool onCurrent);
        [PreserveSig] int GetWindowDesktopId(IntPtr window, out Guid desktop);
        [PreserveSig] int MoveWindowToDesktop(IntPtr window, ref Guid desktop);
    }
}

internal static class PreviewNative
{
    [StructLayout(LayoutKind.Sequential)] internal struct Rect
    {
        internal int Left, Top, Right, Bottom;
        internal Rect(Rectangle rect) { Left = rect.Left; Top = rect.Top; Right = rect.Right; Bottom = rect.Bottom; }
    }
    [StructLayout(LayoutKind.Sequential)] internal struct Size { internal int Width, Height; }
    [StructLayout(LayoutKind.Sequential)] internal struct ThumbnailProperties
    {
        internal uint Flags;
        internal Rect Destination, Source;
        internal byte Opacity;
        [MarshalAs(UnmanagedType.Bool)] internal bool Visible;
        [MarshalAs(UnmanagedType.Bool)] internal bool SourceClientOnly;
    }
    internal delegate bool EnumWindowProc(IntPtr window, IntPtr data);
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindowProc callback, IntPtr data);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] internal static extern bool ShowWindowAsync(IntPtr window, int command);
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern IntPtr GetLastActivePopup(IntPtr window);
    [DllImport("user32.dll")] internal static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] internal static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll")] internal static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("dwmapi.dll")] internal static extern int DwmRegisterThumbnail(IntPtr destination, IntPtr source, out IntPtr thumbnail);
    [DllImport("dwmapi.dll")] internal static extern int DwmUnregisterThumbnail(IntPtr thumbnail);
    [DllImport("dwmapi.dll")] internal static extern int DwmUpdateThumbnailProperties(IntPtr thumbnail, ref ThumbnailProperties properties);
    [DllImport("dwmapi.dll")] internal static extern int DwmQueryThumbnailSourceSize(IntPtr thumbnail, out Size size);
    [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(IntPtr window, uint attribute, out int value, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool SystemParametersInfo(uint action, int size, StringBuilder value, uint flags);
}
