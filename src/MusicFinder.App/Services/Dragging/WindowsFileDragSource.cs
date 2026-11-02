using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;

namespace MusicFinder.App.Services.Dragging;

/// <summary>
/// Standard Windows file-drop semantics: a <c>FileDrop</c> (CF_HDROP) data object carrying the real paths, offered as
/// Copy or Link only. Move is deliberately not offered, so no target can ever move or delete a file out of the library.
/// </summary>
public sealed class WindowsFileDragSource : IFileDragSource
{
    private const int DropEffectCopy = 1, DropEffectLink = 4;

    public DragOutcome StartDrag(IReadOnlyList<string> paths, object source)
    {
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, paths.ToArray());
        // Shell convention: tells the target which effect the source prefers (Copy), as Explorer does.
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(DropEffectCopy)));

        Serilog.Log.Information("Starting drag of {Count} file(s)", paths.Count);
        var started = DateTime.UtcNow;
        var effect = DragDrop.DoDragDrop(source as DependencyObject ?? System.Windows.Application.Current.MainWindow!, data,
            DragDropEffects.Copy | DragDropEffects.Link);
        var completed = DateTime.UtcNow;
        var target = ForegroundProcessName();
        Serilog.Log.Information("Drag of {Count} file(s) ended with effect {Effect} after {Ms} ms; target process {Target}",
            paths.Count, effect, (completed - started).TotalMilliseconds, target ?? "(none)");

        return new DragOutcome(effect switch
        {
            DragDropEffects.None => "None",
            _ when effect.HasFlag(DragDropEffects.Copy) => "Copy",
            _ when effect.HasFlag(DragDropEffects.Link) => "Link",
            _ => effect.ToString()
        }, started, completed, target);
    }

    /// <summary>
    /// The application under the mouse cursor when the drop happened (the cursor is still over the target when the
    /// drag loop returns). A hint about the target, not a fact: it can be a different process than the one that
    /// consumes the files. Store apps are hosted by ApplicationFrameHost, so its real app window is looked up inside it.
    /// </summary>
    private static string? ForegroundProcessName()
    {
        try
        {
            if (!GetCursorPos(out var pt)) return null;
            var hwnd = WindowFromPoint(pt);
            if (hwnd == IntPtr.Zero) return null;
            var root = GetAncestor(hwnd, GaRootOwner);
            GetWindowThreadProcessId(root, out var pid);
            if (pid == 0 || pid == Environment.ProcessId) return null; // dropped back onto ourselves: no external target

            var name = NameOf(pid);
            if (string.Equals(name, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
            {
                uint hosted = 0;
                EnumChildWindows(root, (child, _) =>
                {
                    GetWindowThreadProcessId(child, out var childPid);
                    if (childPid != 0 && childPid != pid) { hosted = childPid; return false; }
                    return true;
                }, IntPtr.Zero);
                if (hosted != 0) name = NameOf(hosted) ?? name;
            }
            return name;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static string? NameOf(uint pid)
    {
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return null; }
    }

    private const uint GaRootOwner = 3;
    private delegate bool EnumChildProc(IntPtr hwnd, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point p);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point p);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumChildProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
