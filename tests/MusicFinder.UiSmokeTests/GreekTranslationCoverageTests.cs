using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace MusicFinder.UiSmokeTests;

/// <summary>
/// Guards the Greek translation: every English text the app can show must have an entry in Strings.el.resx. A missing entry would
/// not crash anything (English is shown instead) but it would leave a hole in the Greek UI, so it fails here.
/// </summary>
public class GreekTranslationCoverageTests
{
    private static string AppDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CLAUDE.md"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "MusicFinder.App");
    }

    private static HashSet<string> Translated()
    {
        var doc = XDocument.Load(Path.Combine(AppDir(), "Resources", "Strings.el.resx"));
        return doc.Root!.Elements("data").Select(d => (string)d.Attribute("name")!).ToHashSet(StringComparer.Ordinal);
    }

    [Fact]
    public void EveryLocTCallInTheCode_HasAGreekEntry()
    {
        var have = Translated();
        var call = new Regex("""Loc\.T\(\s*"((?:[^"\\]|\\.)*)"\s*[,)]""");
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(AppDir(), "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\")))
            foreach (Match m in call.Matches(File.ReadAllText(file)))
            {
                var key = Regex.Unescape(m.Groups[1].Value).Trim();
                if (key.Length > 0 && !have.Contains(key)) missing.Add($"{key}   ({Path.GetFileName(file)})");
            }
        Assert.True(missing.Count == 0, "No Greek entry for:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void EveryLiteralTextInTheXaml_HasAGreekEntry()
    {
        var have = Translated();
        var attr = new Regex("""\b(?:Text|Content|Header|ToolTip|Title|AutomationProperties\.Name|AutomationProperties\.HelpText)="([^"{][^"]*)"(?=[\s/>])""");
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(AppDir(), "*.xaml", SearchOption.AllDirectories).Where(f => !f.Contains("\\Themes\\") && !f.Contains("\\obj\\")))
            foreach (Match m in attr.Matches(File.ReadAllText(file)))
            {
                var key = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Trim();
                // Only texts with letters: icons, symbols and numbers need no translation. Product names stay as they are.
                if (!key.Any(char.IsLetter) || key is "MusicFinder" or "BPM" or "Traktor" or "VirtualDJ") continue;
                if (!have.Contains(key)) missing.Add($"{key}   ({Path.GetFileName(file)})");
            }
        Assert.True(missing.Count == 0, "No Greek entry for:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void GreekEntries_KeepTheirPlaceholders()
    {
        var doc = XDocument.Load(Path.Combine(AppDir(), "Resources", "Strings.el.resx"));
        var ph = new Regex(@"\{\d+(?::[^}]*)?\}");
        var bad = new List<string>();
        foreach (var d in doc.Root!.Elements("data"))
        {
            var key = (string)d.Attribute("name")!;
            var value = (string)d.Element("value")!;
            var a = ph.Matches(key).Select(m => m.Value).OrderBy(x => x).ToList();
            var b = ph.Matches(value).Select(m => m.Value).OrderBy(x => x).ToList();
            if (!a.SequenceEqual(b)) bad.Add(key);
        }
        Assert.True(bad.Count == 0, "Placeholders differ for:\n" + string.Join("\n", bad));
    }

    [Fact]
    public void GreekIsPickedWhenChosen_AndEnglishOtherwise()
    {
        var (c, ui, dc, dui) = (System.Globalization.CultureInfo.CurrentCulture, System.Globalization.CultureInfo.CurrentUICulture, System.Globalization.CultureInfo.DefaultThreadCurrentCulture, System.Globalization.CultureInfo.DefaultThreadCurrentUICulture);
        try
        {
            MusicFinder.App.Localization.Loc.Initialize("el");
            Assert.Equal("Ρυθμίσεις", MusicFinder.App.Localization.Loc.T("Settings"));
            Assert.Equal("a text nobody translated", MusicFinder.App.Localization.Loc.T("a text nobody translated"));
            MusicFinder.App.Localization.Loc.Initialize("en");
            Assert.Equal("Settings", MusicFinder.App.Localization.Loc.T("Settings"));
        }
        finally
        {
            MusicFinder.App.Localization.Loc.Initialize("en");
            System.Globalization.CultureInfo.CurrentCulture = c; System.Globalization.CultureInfo.CurrentUICulture = ui;
            System.Globalization.CultureInfo.DefaultThreadCurrentCulture = dc; System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = dui;
        }
    }
}

public class UiTranslatorTests
{
    [Fact]
    public Task LiteralXamlTexts_AreTranslatedWhenTheWindowLoads() => Ui.RunAsync(async () =>
    {
        var (c, ui, dc, dui) = (System.Globalization.CultureInfo.CurrentCulture, System.Globalization.CultureInfo.CurrentUICulture, System.Globalization.CultureInfo.DefaultThreadCurrentCulture, System.Globalization.CultureInfo.DefaultThreadCurrentUICulture);
        try
        {
            MusicFinder.App.Localization.Loc.Initialize("el");
            MusicFinder.App.Localization.UiTranslator.Install();
            var window = (System.Windows.Window)System.Windows.Markup.XamlReader.Parse(
                "<Window xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Title='Settings' Width='200' Height='200' ShowInTaskbar='False'>" +
                "<StackPanel><Button x:Name='B' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' Content='Reset' ToolTip='Search'/><TextBlock x:Name='T' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' Text='Folders'/></StackPanel></Window>");
            window.Show();
            await Task.Delay(300);
            try
            {
                Assert.Equal("Ρυθμίσεις", window.Title);
                var panel = (System.Windows.Controls.StackPanel)window.Content;
                Assert.Equal("Επαναφορά", ((System.Windows.Controls.Button)panel.Children[0]).Content);
                Assert.Equal("Φάκελοι", ((System.Windows.Controls.TextBlock)panel.Children[1]).Text);
            }
            finally { window.Close(); }
        }
        finally
        {
            MusicFinder.App.Localization.Loc.Initialize("en"); // the rest of the suite expects English
            System.Globalization.CultureInfo.CurrentCulture = c; System.Globalization.CultureInfo.CurrentUICulture = ui;
            System.Globalization.CultureInfo.DefaultThreadCurrentCulture = dc; System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = dui;
        }
    });
}
