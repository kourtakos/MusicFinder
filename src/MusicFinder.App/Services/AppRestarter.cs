using System.Diagnostics;
using System.Windows;

namespace MusicFinder.App.Services;

/// <summary>Closes MusicFinder and starts it again with the same arguments, so a prepared restore can be applied.</summary>
public interface IAppRestarter
{
    void Restart();
}

public sealed class AppRestarter : IAppRestarter
{
    public void Restart()
    {
        var exe = Environment.ProcessPath;
        if (exe is not null)
        {
            var args = string.Join(" ", Environment.GetCommandLineArgs().Skip(1).Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
            // the new process waits for this one to release its files (the swap retries while they are still in use)
            Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true });
        }
        System.Windows.Application.Current.Shutdown();
    }
}
