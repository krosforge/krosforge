using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Avalonia.Interactivity;
using Krosforge.Contracts;
using Krosforge.Launcher;
using Xunit;
[assembly: AvaloniaTestApplication(typeof(Krosforge.Tests.TestApplication))]
namespace Krosforge.Tests;

public static class TestApplication
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
public sealed class WindowTests
{
    [AvaloniaFact]
    public void ListsFourIndependentProfilesWithoutStartingNativeProcesses()
    {
        var connection = new Connection();
        var window = new MainWindow(connection);
        window.Show();
        Assert.Equal(4, window.GetVisualDescendants().OfType<CheckBox>().Count());
        Assert.Contains(window.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, Text.Get("OpenSelected")));
        Assert.Equal(new[] { "snapshot" }, connection.Operations);
        window.Close();
    }
    [AvaloniaFact]
    public void OnlySelectedProfilesAreLaunched()
    {
        var connection = new Connection();
        var window = new MainWindow(connection);
        window.Show();
        var checks = window.GetVisualDescendants().OfType<CheckBox>().ToArray();
        checks[0].IsChecked = false;
        checks[1].IsChecked = true;
        checks[3].IsChecked = true;
        var play = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, Text.Get("OpenSelected")));
        Assert.True(play.IsEnabled);
        play.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(new[] { connection.Profiles[1].Id, connection.Profiles[3].Id }.Order(), connection.Launched.Order());
        window.Close();
    }
    [AvaloniaFact]
    public void DeselectingAllProfilesDisablesPlay()
    {
        var window = new MainWindow(new Connection());
        window.Show();
        foreach (var check in window.GetVisualDescendants().OfType<CheckBox>()) check.IsChecked = false;
        Assert.False(window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, Text.Get("OpenSelected"))).IsEnabled);
        window.Close();
    }
    [AvaloniaFact]
    public void MissingInstallationKeepsProfilesVisibleAndPlayDisabled()
    {
        var window = new MainWindow(new Connection { Installation = null });
        window.Show();
        Assert.Equal(4, window.GetVisualDescendants().OfType<CheckBox>().Count());
        Assert.False(window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, Text.Get("OpenSelected"))).IsEnabled);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), b => b.Text == Text.Get("ClientMissing"));
        window.Close();
    }
    [AvaloniaFact]
    public void EmptyProfileListOffersCreationWithoutLaunchingAnything()
    {
        var connection = new Connection { Profiles = [] };
        var window = new MainWindow(connection);
        window.Show();
        Assert.Empty(window.GetVisualDescendants().OfType<CheckBox>());
        Assert.Contains(window.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, Text.Get("CreateProfile")));
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), b => b.Text == Text.Get("NoProfiles"));
        Assert.Equal(new[] { "snapshot" }, connection.Operations);
        window.Close();
    }
    private sealed class Connection : IRuntimeConnection
    {
        public List<string> Operations { get; } = [];
        public List<Guid> Launched { get; } = [];
        public Profile[] Profiles { get; init; } = Enumerable.Range(1, 4).Select(i => new Profile(Guid.NewGuid(), "Perfil " + i, i)).ToArray();
        public Installation? Installation { get; init; } = new(@"C:\Client", "3.7.14.18");
        public Task<T> CallAsync<T>(string operation, Guid? id = null, string? value = null, string? confirmation = null)
        {
            Operations.Add(operation);
            if (operation == "launch") Launched.Add(id!.Value);
            object result = operation == "snapshot" ? new Snapshot(Profiles, Installation) : true;
            return Task.FromResult((T)result);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
