using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Krosforge.Contracts;
namespace Krosforge.Launcher;

public sealed partial class MainWindow
{
    private readonly List<Action> localizationBindings = [];
    private readonly LanguagePreferences languagePreferences;
    private string languagePreference;
    private Snapshot? lastSnapshot;
    private string? lastError;

    private TextBlock LocalizedLabel(string key, string? style = null)
    {
        var label = Label(Text.Get(key), style);
        localizationBindings.Add(() => label.Text = Text.Get(key));
        return label;
    }
    private Button LocalizedAction(string key, Func<Task> action, string? style = null)
    {
        var button = Action(Text.Get(key), action, style);
        localizationBindings.Add(() => button.Content = Text.Get(key));
        return button;
    }
    internal void ChangeLanguage(string preference)
    {
        try { languagePreferences.Save(preference); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new IOException("ErrorLanguageSave", error); }
        languagePreference = preference;
        Text.Use(languagePreferences.Resolve(preference));
        Title = Text.Get("Title");
        foreach (var update in localizationBindings) update();
        if (lastSnapshot != null) RenderSnapshot(lastSnapshot);
        if (status.IsVisible && lastError != null) status.Text = Text.Error(lastError);
        UpdateSelection();
    }
    private async Task ChooseLanguage()
    {
        var choices = new[] { "auto" }.Concat(LanguagePreferences.SupportedLanguages).Select(code => new LanguageChoice(code,
            code == "auto" ? string.Format(Text.Get("AutomaticLanguage"), LanguageName(languagePreferences.Resolve("auto").Name)) : LanguageName(code))).ToArray();
        var dialog = new Window { Title = Text.Get("Language"), Icon = Icon, Width = 440, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var content = new StackPanel { Spacing = 18, Margin = new Thickness(28) };
        content.Children.Add(new TextBlock { Text = Text.Get("Language"), FontSize = 22 });
        content.Children.Add(Label(Text.Get("LanguageHelp"), "muted"));
        var languages = new ComboBox { ItemsSource = choices, SelectedItem = choices.Single(c => c.Code == languagePreference), HorizontalAlignment = HorizontalAlignment.Stretch };
        Avalonia.Automation.AutomationProperties.SetName(languages, Text.Get("Language"));
        content.Children.Add(languages);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = Text.Get("Cancel"), IsCancel = true };
        cancel.Click += (_, _) => dialog.Close((string?)null);
        var apply = new Button { Content = Text.Get("Apply"), IsDefault = true, Classes = { "primary" } };
        apply.Click += (_, _) => dialog.Close((languages.SelectedItem as LanguageChoice)?.Code);
        buttons.Children.Add(cancel);
        buttons.Children.Add(apply);
        content.Children.Add(buttons);
        dialog.Content = content;
        var preference = await dialog.ShowDialog<string?>(this);
        if (preference != null) ChangeLanguage(preference);
    }
    private static string LanguageName(string code) => code switch
    {
        "fr" => "Français", "de" => "Deutsch", "es" => "Español", "pt" => "Português", _ => "English"
    };
    private sealed record LanguageChoice(string Code, string Name)
    {
        public override string ToString() => Name;
    }
}
