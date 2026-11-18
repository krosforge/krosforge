using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Krosforge.Contracts;
using System.Text.Json;
namespace Krosforge.Launcher;

public sealed partial class MainWindow
{
    private async Task ConfigureServer()
    {
        await Refresh();
        var active = lastSnapshot!.HasActiveClients;
        var dialog = new Window { Title = Text.Get("ConfigureServer"), Icon = Icon, Width = 440, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var content = new StackPanel { Spacing = 18, Margin = new Thickness(28) };
        content.Children.Add(Label(Text.Get("ServerName")));
        var name = new TextBox { Name = "ServerName", Text = lastSnapshot.Server?.Name ?? "Krosforge", MaxLength = 40, IsEnabled = !active };
        Avalonia.Automation.AutomationProperties.SetName(name, Text.Get("ServerName"));
        content.Children.Add(name);
        content.Children.Add(Label(Text.Get("ServerMode"), "muted"));
        NumericUpDown Rate(string key, decimal value)
        {
            content.Children.Add(Label(Text.Get(key)));
            var input = new NumericUpDown { Minimum = 0, Maximum = 1000, Increment = 0.5m, Value = value, IsEnabled = !active };
            Avalonia.Automation.AutomationProperties.SetName(input, Text.Get(key));
            content.Children.Add(input);
            return input;
        }
        var experience = Rate("ExperienceMultiplier", lastSnapshot.Server?.ExperienceMultiplier ?? 1m);
        var drops = Rate("DropMultiplier", lastSnapshot.Server?.DropMultiplier ?? 1m);
        if (active) content.Children.Add(Label(Text.Get("ErrorCloseGameFirst")));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = Text.Get("Cancel"), IsCancel = true };
        cancel.Click += (_, _) => dialog.Close((ServerSettings?)null);
        var save = new Button { Content = Text.Get("SaveServer"), IsDefault = true, IsEnabled = !active, Classes = { "primary" } };
        save.Click += (_, _) => dialog.Close(new ServerSettings(name.Text ?? "", experience.Value ?? 1m, drops.Value ?? 1m));
        buttons.Children.Add(cancel); buttons.Children.Add(save); content.Children.Add(buttons);
        dialog.Content = content;
        var value = await dialog.ShowDialog<ServerSettings?>(this);
        if (value is null) return;
        await runtime.CallAsync<ServerSettings>("configure-server-settings", value: JsonSerializer.Serialize(value));
        await Refresh();
    }
}
