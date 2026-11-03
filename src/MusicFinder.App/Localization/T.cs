using System.Windows.Markup;

namespace MusicFinder.App.Localization;

/// <summary>
/// XAML: <c>TargetNullValue={loc:T '(empty)'}</c>. Needed only where a text is not a plain literal on an element;
/// plain literal texts are translated by <see cref="UiTranslator"/> without any markup.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class T : MarkupExtension
{
    public T(string text) => Text = text;

    public string Text { get; }

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Text);
}
