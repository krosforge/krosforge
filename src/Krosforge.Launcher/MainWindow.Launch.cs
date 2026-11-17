using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Krosforge.Contracts;
namespace Krosforge.Launcher;

public sealed partial class MainWindow
{
    private async Task LaunchSelected()
    {
        try { foreach (var id in selected.ToArray()) await LaunchProfile(id); }
        catch (OperationCanceledException) { }
    }
    private async Task LaunchProfile(Guid profileId)
    {
        using var cancellation = new CancellationTokenSource();
        var dialog = new Window { Title = Text.Get("Play"), Icon = Icon, Width = 460, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var label = new TextBlock { Text = Text.Get("LaunchPreparing"), TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var progress = new ProgressBar { IsIndeterminate = true, MinHeight = 8 };
        var cancel = new Button { Content = Text.Get("Cancel"), HorizontalAlignment = HorizontalAlignment.Right };
        var panel = new StackPanel { Spacing = 20, Margin = new Thickness(28) };
        panel.Children.Add(label); panel.Children.Add(progress); panel.Children.Add(cancel);
        dialog.Content = panel;
        cancel.Click += (_, _) => cancellation.Cancel();
        var completed = false;
        LaunchStatus? current = null;
        dialog.Closing += (_, args) =>
        {
            if (!completed)
            {
                args.Cancel = true;
                if (current is null || current.Stage == "preparing") cancellation.Cancel();
            }
        };
        dialog.Show(this);
        try
        {
            current = await runtime.CallAsync<LaunchStatus>("launch", profileId, Text.Culture.TwoLetterISOLanguageName);
            var started = DateTime.UtcNow;
            while (true)
            {
                if (current.Stage is "failed" or "cancelled") throw new IOException(LaunchError(current.Error));
                if (current.Stage == "exited") throw new IOException("ErrorLaunchFailed");
                if (current.Stage == "server-selection") return;
                cancellation.Token.ThrowIfCancellationRequested();
                label.Text = Text.Get(current.Stage switch { "preparing" => "LaunchPreparing", "authenticating" => "LaunchAuthenticating", _ => "LaunchStarting" });
                progress.IsIndeterminate = current.Stage != "preparing" || current.TotalBytes == 0;
                if (!progress.IsIndeterminate) progress.Value = (double)current.BytesCopied / current.TotalBytes * 100;
                cancel.IsEnabled = current.Stage == "preparing";
                if (current.Stage == "preparing") started = DateTime.UtcNow;
                else if (DateTime.UtcNow - started > TimeSpan.FromMinutes(3)) throw new IOException("ErrorLaunchFailed");
                await Task.Delay(300, cancellation.Token);
                current = await runtime.CallAsync<LaunchStatus>("launch-status", profileId);
            }
        }
        catch (OperationCanceledException)
        {
            if (current?.Stage == "preparing" && !await runtime.CallAsync<bool>("cancel-launch", profileId))
                throw new IOException("ErrorCloseGameFirst");
            throw;
        }
        catch (InvalidOperationException error) when (error.Message.StartsWith("Error", StringComparison.Ordinal))
        {
            throw new IOException(LaunchError(error.Message), error);
        }
        finally { completed = true; dialog.Close(); }
    }
    private static string LaunchError(string? error) => error ?? "ErrorLaunchFailed";
}
