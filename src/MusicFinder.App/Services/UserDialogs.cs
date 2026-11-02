using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;

namespace MusicFinder.App.Services;

public interface IUserDialogs
{
    string? PickFolder();
    bool Confirm(string title, string message);
    void ShowError(string title, string message);
    void ShowInfo(string title, string message);
    void RevealInExplorer(string path);
    void CopyToClipboard(string text);
    /// <summary>Shows the tag editor as a modal dialog over the main window.</summary>
    void ShowTagEditor(ViewModels.TagEditorViewModel viewModel);
}

public sealed class UserDialogs : IUserDialogs
{
    public string? PickFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Choose a music folder to index" };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    public bool Confirm(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public void ShowError(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public void ShowInfo(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public void RevealInExplorer(string path)
    {
        try
        {
            var arg = File.Exists(path) ? $"/select,\"{path}\"" : $"\"{path}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", arg) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowError("Open in Explorer", ex.Message);
        }
    }

    public void ShowTagEditor(ViewModels.TagEditorViewModel viewModel) =>
        new Views.TagEditorWindow(viewModel) { Owner = System.Windows.Application.Current?.MainWindow }.ShowDialog();

    public void CopyToClipboard(string text)
    {
        try { Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.COMException) { /* clipboard locked by another app; ignore */ }
    }
}
