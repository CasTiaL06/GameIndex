using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using GameIndex.Core;

namespace GameIndex.Services;

public sealed record JumpItem(string Title, string Arguments, string? IconPath);

/// <summary>
/// Windows taskbar integration:
///  * explicit AppUserModelID, so the taskbar button / pinned icon / jump list always belong to GameIndex,
///  * a jump list (right-click the taskbar icon): recent games, favorite games, "Open GameIndex" and "Scan for games" tasks,
///  * <see cref="InstanceBridge"/> so a jump-list click is forwarded to the already running instance.
/// Windows does not allow an app to pin itself; right-click the taskbar icon → "Pin to taskbar".
/// </summary>
public static class Taskbar
{
    public const string AppId = "GameIndex.Launcher";

    [DllImport("shell32.dll")] private static extern int SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string appId);

    public static void SetAppId() { try { SetCurrentProcessExplicitAppUserModelID(AppId); } catch (Exception ex) { Log.Warn("AppUserModelID failed: " + ex.Message); } }

    // ---------- COM interop (jump list) ----------
    [ComImport, Guid("77F10CF0-3DB5-4966-B520-B7C54FD35ED6")] private class CDestinationList { }
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")] private class CShellLink { }

    [ComImport, Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectArray
    {
        void GetCount(out uint count);
        void GetAt(uint index, [In] ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object obj);
    }

    [ComImport, Guid("5632B1A4-E38A-400A-928A-D4CD63230295"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, [In] ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object obj);
        void AddObject([MarshalAs(UnmanagedType.Interface)] object obj);
        void AddFromArray([MarshalAs(UnmanagedType.Interface)] IObjectArray source);
        void RemoveObjectAt(uint index);
        void Clear();
    }

    [ComImport, Guid("6332DEBF-87B5-4670-90C0-5E57B408A49E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICustomDestinationList
    {
        void SetAppID([MarshalAs(UnmanagedType.LPWStr)] string appId);
        [PreserveSig] int BeginList(out uint maxSlots, [In] ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object removed);
        [PreserveSig] int AppendCategory([MarshalAs(UnmanagedType.LPWStr)] string category, [MarshalAs(UnmanagedType.Interface)] IObjectArray items);
        void AppendKnownCategory(int category);
        [PreserveSig] int AddUserTasks([MarshalAs(UnmanagedType.Interface)] IObjectArray tasks);
        void CommitList();
        void GetRemovedDestinations([In] ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object removed);
        void DeleteList([MarshalAs(UnmanagedType.LPWStr)] string appId);
        void AbortList();
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int max, IntPtr findData, uint flags);
        void GetIDList(out IntPtr pidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int max);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int max);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int max);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int cmd);
        void SetShowCmd(int cmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int max, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)] private struct PropertyKey { public Guid FormatId; public uint PropertyId; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] private struct PropVariant { [FieldOffset(0)] public ushort Type; [FieldOffset(8)] public IntPtr Pointer; }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue([In] ref PropertyKey key, out PropVariant value);
        void SetValue([In] ref PropertyKey key, [In] ref PropVariant value);
        void Commit();
    }

    private static IObjectCollection NewCollection() =>
        (IObjectCollection)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("2D3468C1-36A7-43B6-AC24-D3F02FD9607A"))!)!;

    private static object MakeLink(string title, string arguments, string? iconPath)
    {
        var exe = Environment.ProcessPath!;
        var link = (IShellLinkW)new CShellLink();
        link.SetPath(exe);
        link.SetArguments(arguments);
        link.SetWorkingDirectory(Path.GetDirectoryName(exe)!);
        link.SetIconLocation(!string.IsNullOrEmpty(iconPath) && File.Exists(iconPath) ? iconPath : exe, 0);
        link.SetDescription(title);

        var store = (IPropertyStore)link;
        var key = new PropertyKey { FormatId = new Guid("F29F85E0-4FF9-4CA3-8B6D-48C42D9FCF55"), PropertyId = 2 };   // System.Title
        var value = new PropVariant { Type = 31 /* VT_LPWSTR */, Pointer = Marshal.StringToCoTaskMemUni(title) };
        try { store.SetValue(ref key, ref value); store.Commit(); }
        finally { Marshal.FreeCoTaskMem(value.Pointer); }
        return link;
    }

    /// <summary>Rebuilds the jump list. Must run on the UI (STA) thread. Failures are logged and never fatal.</summary>
    public static void Update(IEnumerable<JumpItem> recent, IEnumerable<JumpItem> favorites)
    {
        try
        {
            var list = (ICustomDestinationList)new CDestinationList();
            list.SetAppID(AppId);
            var iid = typeof(IObjectArray).GUID;
            if (list.BeginList(out var maxSlots, ref iid, out _) != 0) return;

            var budget = (int)Math.Max(0, maxSlots);
            void Category(string title, IEnumerable<JumpItem> items)
            {
                var take = items.Take(Math.Min(budget, 6)).ToList();
                if (take.Count == 0) return;
                var c = NewCollection();
                foreach (var i in take) c.AddObject(MakeLink(i.Title, i.Arguments, i.IconPath));
                list.AppendCategory(title, (IObjectArray)c);
                budget -= take.Count;
            }
            Category(Loc.T("Recent games"), recent);
            Category(Loc.T("Favorite games"), favorites);

            var tasks = NewCollection();
            tasks.AddObject(MakeLink(Loc.T("Open GameIndex"), "--show", null));
            tasks.AddObject(MakeLink(Loc.T("Scan for games"), "--scan", null));
            list.AddUserTasks((IObjectArray)tasks);
            list.CommitList();
        }
        catch (Exception ex) { Log.Warn("Jump list update failed: " + ex.Message); }
    }

    public static void Clear()
    {
        try { ((ICustomDestinationList)new CDestinationList()).DeleteList(AppId); } catch { }
    }
}

/// <summary>Single-instance command channel (named pipe): jump-list clicks reach the instance that is already running.</summary>
public static class InstanceBridge
{
    public static bool TrySend(string pipeName, string message)
    {
        try
        {
            using var c = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
            c.Connect(1500);
            using var w = new StreamWriter(c) { AutoFlush = true };
            w.WriteLine(message);
            return true;
        }
        catch { return false; }
    }

    public static void StartServer(string pipeName, Action<string> onMessage)
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    using var s = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await s.WaitForConnectionAsync();
                    using var r = new StreamReader(s);
                    var line = await r.ReadLineAsync();
                    if (!string.IsNullOrWhiteSpace(line)) onMessage(line);
                }
                catch { await Task.Delay(500); }
            }
        });
    }

    /// <summary>Command line → message: "--launch 12" → "launch:12", "--scan" → "scan", otherwise "show".</summary>
    public static string FromArgs(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--launch" && i + 1 < args.Length && long.TryParse(args[i + 1], out var id)) return $"launch:{id}";
            if (args[i] == "--scan") return "scan";
        }
        return "show";
    }
}
