using MusicFinder.App.ViewModels;

namespace MusicFinder.UiSmokeTests;

public class FolderScopeTests
{
    [Fact]
    public Task ChoosingAFolder_SwitchesOffAGlobalSearchLeftByAHealthFinding() => Ui.RunAsync(async () =>
    {
        var app = await UiTestApp.CreateWithFilesAsync(null, ("a.mp3", "A", "X", "Pop"), ("b.mp3", "B", "Y", "Pop"));
        await using var _ = app;
        app.Window.Show();
        await app.Main.InitializeAsync();
        var library = app.Main.Library;

        library.ShowFiltered("issue:any");
        Assert.True(library.Tree.GlobalSearch);

        var node = library.Tree.Roots[0];
        node.IsSelected = true;

        Assert.False(library.Tree.GlobalSearch);
        Assert.Equal(node.Id, library.Tree.SelectedFolderId);
    });
}
