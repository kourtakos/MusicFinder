using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;

namespace MusicFinder.App.Localization;

/// <summary>
/// Translates the plain English texts written in the XAML. Every element is looked at once, when it has loaded (this includes items made from
/// templates, menus and pop-ups). Only literal values are touched: anything that comes from a binding, such as a track title, is left alone.
/// </summary>
public static class UiTranslator
{
    private static bool _installed;


    public static void Install()
    {
        if (_installed || !Loc.IsTranslated) return;
        _installed = true;
        var handler = new RoutedEventHandler((s, _) =>
        {
            var element = (FrameworkElement)s;
            Translate(element);
            if (element is Window window) TranslateTree(window);
        });
        // Registered per concrete type: a handler on the FrameworkElement base class is not called for most controls.
        foreach (var type in new[]
                 {
                     typeof(Window), typeof(TextBlock), typeof(Button), typeof(System.Windows.Controls.Primitives.ToggleButton), typeof(CheckBox),
                     typeof(RadioButton), typeof(Label), typeof(MenuItem), typeof(TabItem), typeof(GroupBox), typeof(Expander), typeof(DataGrid),
                     typeof(ComboBoxItem), typeof(ListBoxItem), typeof(ContentControl), typeof(TextBox), typeof(Border), typeof(Slider),
                 })
            EventManager.RegisterClassHandler(type, FrameworkElement.LoadedEvent, handler, true);
    }

    /// <summary>Translates an element and everything below it, logical and visual.</summary>
    public static void TranslateTree(DependencyObject root) => TranslateTree(root, new HashSet<DependencyObject>());

    // Most elements are reached through both trees; each one is translated once (without this the walk doubles at every level and hangs).
    private static void TranslateTree(DependencyObject node, HashSet<DependencyObject> seen)
    {
        if (!seen.Add(node)) return;
        if (node is FrameworkElement fe) Translate(fe);
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()) TranslateTree(child, seen);
        if (node is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D)
            for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); i++)
                TranslateTree(System.Windows.Media.VisualTreeHelper.GetChild(node, i), seen);
    }

    /// <summary>Translates one element's own literal texts (not its children).</summary>
    public static void Translate(FrameworkElement e)
    {
        try
        {
            switch (e)
            {
                case TextBlock tb:
                    if (tb.Inlines.Count <= 1 && !IsBound(tb, TextBlock.TextProperty) && !string.IsNullOrEmpty(tb.Text)) tb.Text = Loc.Padded(tb.Text);
                    else TranslateInlines(tb.Inlines);
                    break;
                case Window w:
                    if (!IsBound(w, Window.TitleProperty)) w.Title = Loc.T(w.Title);
                    break;
                case HeaderedContentControl hcc:
                    if (hcc.Header is string h && !IsBound(hcc, HeaderedContentControl.HeaderProperty)) hcc.Header = Loc.Padded(h);
                    if (hcc.Content is string hc && !IsBound(hcc, ContentControl.ContentProperty)) hcc.Content = Loc.Padded(hc);
                    break;
                case HeaderedItemsControl hic:
                    if (hic.Header is string hi && !IsBound(hic, HeaderedItemsControl.HeaderProperty)) hic.Header = Loc.Padded(hi);
                    break;
                case ContentControl cc:
                    if (cc.Content is string c && !IsBound(cc, ContentControl.ContentProperty)) cc.Content = Loc.Padded(c);
                    break;
                case DataGrid grid:
                    foreach (var col in grid.Columns)
                        if (col.Header is string ch) col.Header = Loc.Padded(ch);
                    break;
            }

            if (e.ToolTip is string tip && !IsBound(e, FrameworkElement.ToolTipProperty)) e.ToolTip = Loc.Padded(tip);
            var name = AutomationProperties.GetName(e);
            if (!string.IsNullOrEmpty(name) && !IsBound(e, AutomationProperties.NameProperty)) AutomationProperties.SetName(e, Loc.T(name));
            var help = AutomationProperties.GetHelpText(e);
            if (!string.IsNullOrEmpty(help) && !IsBound(e, AutomationProperties.HelpTextProperty)) AutomationProperties.SetHelpText(e, Loc.T(help));
        }
        catch (Exception)
        {
            // A text that cannot be translated stays English; translating must never break a window.
        }
    }

    private static void TranslateInlines(InlineCollection inlines)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case Run run when !IsBound(run, Run.TextProperty) && !string.IsNullOrEmpty(run.Text): run.Text = Loc.Padded(run.Text); break;
                case Span span: TranslateInlines(span.Inlines); break;
            }
        }
    }

    private static bool IsBound(DependencyObject d, DependencyProperty p) => BindingOperations.GetBindingExpressionBase(d, p) is not null;
}
