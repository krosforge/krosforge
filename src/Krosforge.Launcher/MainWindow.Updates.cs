using System.Net.Http;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Krosforge.Contracts;
using Krosforge.Launcher.Updates;
namespace Krosforge.Launcher;

public sealed partial class MainWindow
{
    private void InitializeUpdates(StackPanel links) => links.Children.Add(LocalizedAction("Updates", CheckForUpdates, "subtle"));
    private async Task CheckForUpdates()
    {
        if (!OperatingSystem.IsWindows() || !string.Equals(Path.GetExtension(Environment.ProcessPath), ".exe", StringComparison.OrdinalIgnoreCase) || string.Equals(Path.GetFileName(Environment.ProcessPath), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            await UpdateMessage(Text.Get("UpdateWindowsOnly"));
            return;
        }
        string? staging = null;
        var handedOff = false;
        try
        {
            using var http = ReleaseClient.CreateHttpClient();
            var releases = new ReleaseClient(http);
            var current = ReleaseVersion.Parse(typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? Version);
            var update = await UpdateProgress(Text.Get("UpdateChecking"), (token, _) => releases.CheckAsync(current, token));
            if (update is null) { await UpdateMessage(Text.Get("UpdateUpToDate")); return; }
            if (!await UpdateMessage(string.Format(Text.Get("UpdateOffer"), update.Version), Text.Get("UpdateInstall"))) return;
            if ((await runtime.CallAsync<Snapshot>("snapshot")).HasActiveClients)
            {
                await UpdateMessage(Text.Get("UpdateCloseGames"));
                return;
            }
            staging = UpdateInstaller.NewStagingDirectory();
            var download = await UpdateProgress(Text.Get("UpdateDownloading"), (token, progress) => releases.DownloadAsync(update, staging, progress, token));
            if ((await runtime.CallAsync<Snapshot>("snapshot")).HasActiveClients)
            {
                await UpdateMessage(Text.Get("UpdateCloseGames"));
                return;
            }
            await UpdateProgress(Text.Get("UpdateInstalling"), async (token, _) => { await UpdateInstaller.StartHelperAsync(download, token); return true; });
            handedOff = true;
            Close();
        }
        catch (OperationCanceledException) { ShowError("UpdateCancelled"); }
        catch (Exception error) when (error is IOException or InvalidDataException or HttpRequestException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { ShowError("UpdateFailed"); }
        finally
        {
            if (staging is not null && !handedOff)
            {
                try { UpdateInstaller.DeleteStaging(staging); }
                catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException) { }
            }
        }
    }
    private async Task<T> UpdateProgress<T>(string message, Func<CancellationToken, IProgress<DownloadProgress>, Task<T>> operation)
    {
        using var cancellation = new CancellationTokenSource();
        var dialog = UpdateDialog();
        var panel = new StackPanel { Spacing = 18, Margin = new Thickness(28) };
        var label = Label(message);
        var bar = new ProgressBar { IsIndeterminate = true, Height = 6 };
        var cancel = new Button { Content = Text.Get("Cancel"), HorizontalAlignment = HorizontalAlignment.Right };
        cancel.Click += (_, _) => cancellation.Cancel();
        panel.Children.Add(label); panel.Children.Add(bar); panel.Children.Add(cancel);
        dialog.Content = panel;
        dialog.Closing += (_, _) => cancellation.Cancel();
        var shown = dialog.ShowDialog(this);
        var progress = new Progress<DownloadProgress>(value =>
        {
            bar.IsIndeterminate = false; bar.Maximum = value.TotalBytes; bar.Value = value.ReceivedBytes;
            label.Text = value.ReceivedBytes == value.TotalBytes ? Text.Get("UpdateVerifying") : Text.Get("UpdateDownloading") + " " + (100 * value.ReceivedBytes / value.TotalBytes) + "%";
        });
        try { return await operation(cancellation.Token, progress); }
        catch (OperationCanceledException error) when (!cancellation.IsCancellationRequested)
        { throw new IOException("The update operation timed out.", error); }
        finally { dialog.Close(); await shown; }
    }
    private Window UpdateDialog() => new() { Title = Text.Get("UpdateTitle"), Icon = Icon, Width = 490, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
    private async Task<bool> UpdateMessage(string message, string? confirm = null)
    {
        var dialog = UpdateDialog();
        var panel = new StackPanel { Spacing = 20, Margin = new Thickness(28) };
        panel.Children.Add(Label(message));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Right };
        var close = new Button { Content = Text.Get(confirm is null ? "Close" : "Cancel"), IsCancel = true };
        close.Click += (_, _) => dialog.Close(false);
        buttons.Children.Add(close);
        if (confirm is not null)
        {
            var accept = new Button { Content = confirm, IsDefault = true, Classes = { "primary" } };
            accept.Click += (_, _) => dialog.Close(true);
            buttons.Children.Add(accept);
        }
        panel.Children.Add(buttons); dialog.Content = panel;
        return await dialog.ShowDialog<bool>(this);
    }
}
