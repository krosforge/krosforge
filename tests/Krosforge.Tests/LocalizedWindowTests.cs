using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Krosforge.Contracts;
using Krosforge.Launcher;
using Xunit;
namespace Krosforge.Tests;

public sealed class LocalizedWindowTests
{
    [AvaloniaFact]
    public void SwitchingLanguageUpdatesWindowAndDialogsWithoutChangingSelection()
    {
        var directory = Path.Combine(Path.GetTempPath(), "localized-window-" + Guid.NewGuid().ToString("N"));
        var connection = new Connection();
        var preferences = new LanguagePreferences(directory, CultureInfo.GetCultureInfo("en-US"));
        var window = new MainWindow(connection, preferences);
        window.Show();
        try
        {
            var checks = window.GetVisualDescendants().OfType<CheckBox>().ToArray();
            checks[0].IsChecked = false;
            checks[1].IsChecked = true;
            checks[3].IsChecked = true;
            foreach (var language in LanguagePreferences.SupportedLanguages)
            {
                window.ChangeLanguage(language);
                Assert.Equal(language, Text.Culture.Name);
                Assert.Contains(window.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, Text.Get("Updates")));
                checks = window.GetVisualDescendants().OfType<CheckBox>().ToArray();
                Assert.Equal(new[] { false, true, false, true }, checks.Select(c => c.IsChecked == true));
                Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), b => b.Text == Text.Get("Heading"));
                var play = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, Text.Get("OpenSelected")));
                Assert.True(play.IsEnabled);
                var create = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, Text.Get("CreateProfile")));
                create.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var dialog = Assert.Single(window.OwnedWindows);
                Assert.Equal(Text.Get("CreateProfile"), dialog.Title);
                var cancel = dialog.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, Text.Get("Cancel")));
                cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();
            }
            Assert.Equal(new[] { "snapshot" }, connection.Operations);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
    [AvaloniaFact]
    public void RuntimeErrorsUseSelectedLanguageAndNeverExposeIdentifiers()
    {
        var directory = Path.Combine(Path.GetTempPath(), "localized-error-" + Guid.NewGuid().ToString("N"));
        var window = new MainWindow(new Connection { RejectLaunch = true }, new LanguagePreferences(directory, CultureInfo.GetCultureInfo("fr-FR")));
        window.Show();
        try
        {
            var play = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, Text.Get("OpenSelected")));
            play.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), b => b.Text == "Profil introuvable.");
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>(), b => b.Text == "ErrorProfileNotFound");
            window.ChangeLanguage("de");
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), b => b.Text == "Profil nicht gefunden.");
            Assert.Equal(Text.Get("ErrorUnexpected"), Text.Error("An unknown diagnostic message"));
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    [AvaloniaFact]
    public void FailedPreferenceWriteDoesNotChangeCurrentLanguage()
    {
        var path = Path.Combine(Path.GetTempPath(), "language-file-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(path, "Not a directory");
        var window = new MainWindow(new Connection(), new LanguagePreferences(path, CultureInfo.GetCultureInfo("en-US")));
        window.Show();
        try
        {
            Assert.Throws<IOException>(() => window.ChangeLanguage("fr"));
            Assert.Equal("en", Text.Culture.Name);
            Assert.Contains(window.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, "Create profile"));
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); File.Delete(path); }
    }
    private sealed class Connection : IRuntimeConnection
    {
        public bool RejectLaunch { get; init; }
        public List<string> Operations { get; } = [];
        public Task<T> CallAsync<T>(string operation, Guid? id = null, string? value = null, string? confirmation = null)
        {
            Operations.Add(operation);
            if (RejectLaunch && operation == "launch") return Task.FromException<T>(new InvalidOperationException("ErrorProfileNotFound"));
            object result = operation == "snapshot" ? new Snapshot(Enumerable.Range(1, 4).Select(i => new Profile(Guid.NewGuid(), "Profile " + i, i)).ToArray(), new Installation(@"C:\Client", "3.7.14.18")) : true;
            return Task.FromResult((T)result);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
