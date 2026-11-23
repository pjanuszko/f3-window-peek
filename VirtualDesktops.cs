using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Forms;

internal sealed class DesktopCard
{
    internal Guid Id;
    internal string Name;
    internal bool Current;
}

internal static class DesktopSwitchVerification
{
    internal static int Run(string logPath)
    {
        Action<string> log = delegate(string message) { File.AppendAllText(logPath, message + Environment.NewLine); };
        using (var desktops = new VirtualDesktops(log))
        {
            List<DesktopCard> cards = desktops.Enumerate();
            DesktopCard original = cards.Find(delegate(DesktopCard card) { return card.Current; });
            DesktopCard target = cards.Find(delegate(DesktopCard card) { return !card.Current; });
            if (original == null || target == null) { log("Need two desktops for switch verification"); return 1; }
            bool switched = false, restored = false;
            try
            {
                desktops.Switch(target.Id);
                switched = WaitFor(desktops, target.Id);
                log("Switch to alternate: " + switched);
            }
            finally
            {
                desktops.Switch(original.Id);
                restored = WaitFor(desktops, original.Id);
                log("Restore original: " + restored);
            }
            return switched && restored ? 0 : 1;
        }
    }
    private static bool WaitFor(VirtualDesktops desktops, Guid id)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 2000)
        {
            foreach (var card in desktops.Enumerate()) if (card.Current && card.Id == id)
            {
                // Let the shell finish its transition before reversing it.
                var settle = Stopwatch.StartNew();
                while (settle.ElapsedMilliseconds < 450) { Application.DoEvents(); Thread.Sleep(10); }
                return true;
            }
            Application.DoEvents();
            Thread.Sleep(20);
        }
        return false;
    }
}

// Windows 11 24H2 COM layouts adapted from MScholtes/VirtualDesktop (MIT).
// Source and license are retained in third_party.
internal sealed class VirtualDesktops : IDisposable
{
    private IDesktopManager manager;
    private IViews views;
    private IWindowDesktops windowDesktops;
    private readonly Action<string> trace;
    internal VirtualDesktops(Action<string> trace)
    {
        this.trace = trace;
        try
        {
            object shell = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("C2F03A33-21F5-47FA-B4BB-156362A2F239")));
            try
            {
                Guid service = new Guid("C5E0CDCA-7B6E-41B2-9FC4-D93975CC467B");
                Guid iid = typeof(IDesktopManager).GUID;
                manager = (IDesktopManager)((IShellServices)shell).QueryService(ref service, ref iid);
                iid = typeof(IViews).GUID;
                service = iid;
                views = (IViews)((IShellServices)shell).QueryService(ref service, ref iid);
                windowDesktops = (IWindowDesktops)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("AA509086-5CA9-4C25-8F95-589D3C07B48A")));
            }
            finally { Marshal.ReleaseComObject(shell); }
        }
        catch (COMException ex) { trace("desktops-unavailable hr=" + ex.ErrorCode); }
        catch (InvalidCastException) { trace("desktops-interface-unavailable"); }
    }

    internal List<DesktopCard> Enumerate()
    {
        var result = new List<DesktopCard>();
        if (manager == null) return result;
        try
        {
            IDesktop current = manager.GetCurrentDesktop();
            Guid currentId;
            try { currentId = current.GetId(); }
            finally { Marshal.ReleaseComObject(current); }
            IObjectArray array;
            manager.GetDesktops(out array);
            try
            {
                int count;
                array.GetCount(out count);
                for (int i = 0; i < count; i++)
                {
                    object item;
                    Guid iid = typeof(IDesktop).GUID;
                    array.GetAt(i, ref iid, out item);
                    try
                    {
                        var desktop = (IDesktop)item;
                        Guid id = desktop.GetId();
                        string name = desktop.GetName();
                        result.Add(new DesktopCard { Id = id, Current = id == currentId,
                            Name = string.IsNullOrWhiteSpace(name) ? "Pulpit " + (i + 1) : name });
                    }
                    finally { Marshal.ReleaseComObject(item); }
                }
            }
            finally { Marshal.ReleaseComObject(array); }
        }
        catch (COMException ex) { trace("desktops-refresh-failed hr=" + ex.ErrorCode); }
        return result;
    }

    internal void Switch(Guid id)
    {
        if (manager == null) return;
        try
        {
            IDesktop desktop = manager.FindDesktop(ref id);
            try { manager.SwitchDesktopWithAnimation(desktop); trace("desktop-selected id=" + id); }
            finally { Marshal.ReleaseComObject(desktop); }
        }
        catch (COMException ex) { trace("desktop-switch-failed hr=" + ex.ErrorCode); }
    }

    internal Guid WindowDesktop(IntPtr window)
    {
        Guid id;
        return windowDesktops != null && windowDesktops.GetWindowDesktopId(window, out id) >= 0 ? id : Guid.Empty;
    }

    internal bool IsWindowCurrent(IntPtr window)
    {
        bool current;
        return windowDesktops != null && windowDesktops.IsWindowOnCurrentVirtualDesktop(window, out current) >= 0 && current;
    }

    internal bool MoveWindow(IntPtr window, Guid destination)
    {
        if (manager == null || views == null || !PreviewNative.IsWindow(window)) return false;
        IntPtr view = IntPtr.Zero;
        IDesktop desktop = null;
        try
        {
            if (WindowDesktop(window) == destination) return true;
            int result = views.GetViewForHwnd(window, out view);
            if (result < 0 || view == IntPtr.Zero) { trace("window-move-view-unavailable hr=" + result); return false; }
            if (!manager.CanViewMoveDesktops(view)) { trace("window-move-not-supported"); return false; }
            desktop = manager.FindDesktop(ref destination);
            manager.MoveViewToDesktop(view, desktop);
            bool moved = WindowDesktop(window) == destination;
            trace(moved ? "window-moved desktop=" + destination : "window-move-not-confirmed");
            return moved;
        }
        catch (COMException ex) { trace("window-move-failed hr=" + ex.ErrorCode); return false; }
        finally
        {
            if (view != IntPtr.Zero) Marshal.Release(view);
            if (desktop != null) Marshal.ReleaseComObject(desktop);
        }
    }

    public void Dispose()
    {
        if (windowDesktops != null) { Marshal.ReleaseComObject(windowDesktops); windowDesktops = null; }
        if (views != null) { Marshal.ReleaseComObject(views); views = null; }
        if (manager != null) { Marshal.ReleaseComObject(manager); manager = null; }
    }

    [ComImport, Guid("1841C6D7-4F9D-42C0-AF41-8747538F10E5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IViews
    {
        [PreserveSig] int GetViews(out IntPtr array);
        [PreserveSig] int GetViewsByZOrder(out IntPtr array);
        [PreserveSig] int GetViewsByAppUserModelId([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr array);
        [PreserveSig] int GetViewForHwnd(IntPtr window, out IntPtr view);
    }
    [ComImport, Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWindowDesktops
    {
        [PreserveSig] int IsWindowOnCurrentVirtualDesktop(IntPtr window, [MarshalAs(UnmanagedType.Bool)] out bool current);
        [PreserveSig] int GetWindowDesktopId(IntPtr window, out Guid id);
        [PreserveSig] int MoveWindowToDesktop(IntPtr window, ref Guid id);
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellServices
    {
        [return: MarshalAs(UnmanagedType.IUnknown)] object QueryService(ref Guid service, ref Guid iid);
    }
    [ComImport, Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectArray
    {
        void GetCount(out int count);
        void GetAt(int index, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object item);
    }
    [ComImport, Guid("3F07F4BE-B107-441A-AF0F-39D82529072C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDesktop
    {
        bool IsViewVisible(IntPtr view);
        Guid GetId();
        [return: MarshalAs(UnmanagedType.HString)] string GetName();
        [return: MarshalAs(UnmanagedType.HString)] string GetWallpaperPath();
        bool IsRemote();
    }
    [ComImport, Guid("53F5CA0B-158F-4124-900C-057158060B27"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDesktopManager
    {
        int GetCount();
        void MoveViewToDesktop(IntPtr view, IDesktop desktop);
        bool CanViewMoveDesktops(IntPtr view);
        IDesktop GetCurrentDesktop();
        void GetDesktops(out IObjectArray desktops);
        [PreserveSig] int GetAdjacentDesktop(IDesktop from, int direction, out IDesktop desktop);
        void SwitchDesktop(IDesktop desktop);
        void SwitchDesktopAndMoveForegroundView(IDesktop desktop);
        IDesktop CreateDesktop();
        void MoveDesktop(IDesktop desktop, int index);
        void RemoveDesktop(IDesktop desktop, IDesktop fallback);
        IDesktop FindDesktop(ref Guid id);
        void GetDesktopSwitchIncludeExcludeViews(IDesktop desktop, out IObjectArray include, out IObjectArray exclude);
        void SetDesktopName(IDesktop desktop, [MarshalAs(UnmanagedType.HString)] string name);
        void SetDesktopWallpaper(IDesktop desktop, [MarshalAs(UnmanagedType.HString)] string path);
        void UpdateWallpaperPathForAllDesktops([MarshalAs(UnmanagedType.HString)] string path);
        void CopyDesktopState(IntPtr view, IntPtr otherView);
        void CreateRemoteDesktop([MarshalAs(UnmanagedType.HString)] string path, out IDesktop desktop);
        void SwitchRemoteDesktop(IDesktop desktop, IntPtr type);
        void SwitchDesktopWithAnimation(IDesktop desktop);
    }
}
