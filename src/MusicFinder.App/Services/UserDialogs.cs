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
    /// <summary>Asks the user to agree to what online identification sends. The view model has already saved the settings when it was accepted.</summary>
    void ShowIdentificationConsent(ViewModels.IdentificationConsentViewModel viewModel);
    /// <summary>Shows the online identification window as a modal dialog over the main window.</summary>
    void ShowIdentify(ViewModels.IdentifyViewModel viewModel);
    /// <summary>Shows the BPM and key analysis window as a modal dialog over the main window.</summary>
    void ShowAnalyze(ViewModels.AnalyzeViewModel viewModel);
    /// <summary>Shows the duplicate files review as a modal dialog over the main window.</summary>
    void ShowDuplicates(ViewModels.DuplicatesViewModel viewModel);
    /// <summary>Shows the probable duplicates and other versions review as a modal dialog over the main window.</summary>
    void ShowProbableDuplicates(ViewModels.ProbableDuplicatesViewModel viewModel);
    /// <summary>Shows the batch rename window as a modal dialog over the main window.</summary>
    void ShowRename(ViewModels.RenameViewModel viewModel);
    /// <summary>A save-as picker; null when cancelled.</summary>
    string? PickSaveFile(string title, string filter, string defaultName);
    /// <summary>A file picker; <paramref name="filter"/> is in the Windows form "Name|*.ext|Name2|*.ext2". Null when cancelled.</summary>
    string? PickFile(string title, string filter);
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

    public void ShowIdentificationConsent(ViewModels.IdentificationConsentViewModel viewModel) =>
        new Views.IdentificationConsentWindow(viewModel) { Owner = System.Windows.Application.Current?.MainWindow }.ShowDialog();

    public void ShowIdentify(ViewModels.IdentifyViewModel viewModel) =>
        new Views.IdentifyWindow(viewModel) { Owner = System.Windows.Application.Current?.MainWindow }.ShowDialog();

    public void ShowAnalyze(ViewModels.AnalyzeViewModel viewModel) =>
        new Views.AnalyzeWindow(viewModel) { Owner = System.Windows.Application.Current?.MainWindow }.ShowDialog();

    public void ShowDuplicates(ViewModels.DuplicatesViewModel viewModel) =>
        new Views.DuplicatesWindow(viewModel) { Owner = System.Windows.Application.Current?.MainWindow }.ShowDialog();

    public void ShowProbableDuplicates(ViewModels.ProbableDuplicatesViewModel viewModel) =>
        new Views.ProbableDuplicatesWindow(viewModel) { Owner = System.Windows.Application.Current?.MainWindow }.ShowDialog();

    public void ShowRename(ViewModels.RenameViewModel viewModel) =>
        new Views.RenameWindow(viewModel) { Owner = System.Windows.Application.Current?.MainWindow }.ShowDialog();

    public string? PickSaveFile(string title, string filter, string defaultName)
    {
        var dialog = new SaveFileDialog { Title = title, Filter = filter, FileName = defaultName, OverwritePrompt = true };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickFile(string title, string filter)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public void CopyToClipboard(string text)
    {
        try { Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.COMException) { /* clipboard locked by another app; ignore */ }
    }
}
