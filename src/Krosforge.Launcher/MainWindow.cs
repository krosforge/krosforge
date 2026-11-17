using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Krosforge.Contracts;
namespace Krosforge.Launcher;

public sealed partial class MainWindow : Window
{
    private readonly IRuntimeConnection runtime;
    private readonly StackPanel profiles = new() { Spacing = 10 };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#FFB4AA"), IsVisible = false };
    private readonly TextBlock installation = Label("", "muted");
    private readonly TextBlock installationBuild = Label("", "muted");
    private readonly TextBlock installationState = Label("", "eyebrow");
    private readonly TextBlock profileCount = Label("", "eyebrow");
    private readonly TextBlock selection = Label("", "muted");
    private readonly HashSet<Guid> selected = [];
    private readonly Grid shell = new() { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
    private readonly Button play;
    private bool hasInstallation;
    private bool firstRefresh = true;
    private bool busy;
    private bool closing;
    private bool exitReady;
    private readonly DispatcherTimer heartbeat = new() { Interval = TimeSpan.FromMinutes(1) };
    private static string Version => typeof(App).Assembly.GetName().Version?.ToString(3) ?? "";

    public MainWindow() : this(new RuntimeClient()) { }
    public MainWindow(IRuntimeConnection connection) : this(connection, new LanguagePreferences(AppPaths.Data)) { }
    internal MainWindow(IRuntimeConnection connection, LanguagePreferences preferences)
    {
        runtime = connection;
        languagePreferences = preferences;
        languagePreference = preferences.Load();
        Text.Use(preferences.Resolve(languagePreference));
        Title = Text.Get("Title");
        Width = 1040;
        Height = 760;
        MinWidth = 900;
        MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        using (var icon = AssetLoader.Open(new Uri("avares://Krosforge/Assets/Krosforge.ico")))
            Icon = new WindowIcon(icon);

        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
        brand.Children.Add(Logo(36));
        brand.Children.Add(new TextBlock { Text = "Krosforge", FontSize = 18, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        brand.Children.Add(new TextBlock { Text = "v" + Version, Classes = { "muted" }, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        var links = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Right };
        links.Children.Add(LocalizedAction("About", async () => await Prompt(Text.Get("Title"), string.Format(Text.Get("AboutBody"), Version), null, Text.Get("Close")), "subtle"));
        links.Children.Add(LocalizedAction("Licenses", ExportLicenses, "subtle"));
        links.Children.Add(LocalizedAction("Language", ChooseLanguage, "subtle"));
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(32, 16) };
        header.Children.Add(brand);
        Place(header, links, 0, 1);
        shell.Children.Add(new Border { Child = header, BorderBrush = Brush.Parse("#303740"), BorderThickness = new Thickness(0, 0, 0, 1) });

        var main = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Margin = new Thickness(36, 28, 36, 24) };
        var introduction = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 0, 28) };
        introduction.Children.Add(LocalizedLabel("Heading", "heading"));
        introduction.Children.Add(LocalizedLabel("Tagline", "muted"));
        main.Children.Add(introduction);
        var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("*,28,290") };
        var profileArea = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        var profileHeader = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 14) };
        profileCount.VerticalAlignment = VerticalAlignment.Center;
        profileHeader.Children.Add(profileCount);
        Place(profileHeader, LocalizedAction("CreateProfile", CreateProfile), 0, 1);
        profileArea.Children.Add(profileHeader);
        Place(profileArea, new ScrollViewer { Content = profiles, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled }, 1, 0);
        columns.Children.Add(profileArea);
        var clientPanel = new StackPanel { Spacing = 16 };
        clientPanel.Children.Add(LocalizedLabel("ClientHeading", "eyebrow"));
        clientPanel.Children.Add(installationState);
        installation.TextWrapping = TextWrapping.NoWrap;
        installation.TextTrimming = TextTrimming.CharacterEllipsis;
        installation.FontSize = 13;
        var location = new StackPanel { Spacing = 6 };
        location.Children.Add(installation);
        installationBuild.FontSize = 12;
        location.Children.Add(installationBuild);
        clientPanel.Children.Add(location);
        clientPanel.Children.Add(LocalizedAction("SelectFolder", ChooseInstallation));
        clientPanel.Children.Add(new Border { Height = 1, Background = Brush.Parse("#303740"), Margin = new Thickness(0, 4) });
        clientPanel.Children.Add(LocalizedLabel("LocalHeading"));
        var localHelp = LocalizedLabel("LocalHelp", "muted");
        localHelp.FontSize = 13;
        clientPanel.Children.Add(localHelp);
        Place(columns, new ScrollViewer { Content = Card(clientPanel), HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled }, 0, 2);
        Grid.SetRow(columns, 1);
        main.Children.Add(columns);
        Place(shell, main, 1, 0);

        play = LocalizedAction("OpenSelected", LaunchSelected, "primary");
        play.IsEnabled = false;
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(36, 18) };
        var feedback = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 24, 0) };
        feedback.Children.Add(selection);
        feedback.Children.Add(status);
        footer.Children.Add(feedback);
        Place(footer, play, 0, 1);
        Place(shell, new Border { Child = footer, BorderBrush = Brush.Parse("#303740"), BorderThickness = new Thickness(0, 1, 0, 0) }, 2, 0);
        Content = shell;
        InitializeUpdates(links);
        Opened += async (_, _) =>
        {
            if (!await Updates.UpdateInstaller.CompleteRestartAsync(Environment.GetCommandLineArgs())) { Close(); return; }
            await Run(Refresh);
            if (Environment.GetCommandLineArgs().Contains("--update-failed")) ShowError("UpdateRestartFailed");
        };
        heartbeat.Tick += async (_, _) =>
        {
            if (closing) return;
            try { await runtime.CallAsync<bool>("ping"); }
            catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException or OperationCanceledException)
            { ShowError(error is OperationCanceledException or TimeoutException ? "Timeout" : error.Message); }
        };
        heartbeat.Start();
        Closing += async (_, args) =>
        {
            if (exitReady) return;
            args.Cancel = true;
            if (closing) return;
            closing = true;
            heartbeat.Stop();
            shell.IsEnabled = false;
            try { await runtime.CallAsync<bool>("shutdown"); }
            catch (InvalidOperationException error)
            {
                closing = false;
                shell.IsEnabled = !busy;
                heartbeat.Start();
                ShowError(error.Message == "ErrorCloseGameFirst" ? Text.Get("ErrorCloseGameFirst") : error.Message);
                return;
            }
            catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException) { }
            await runtime.DisposeAsync();
            exitReady = true;
            Dispatcher.UIThread.Post(Close);
        };
    }
    private static TextBlock Label(string text, string? style = null)
    {
        var label = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        if (style != null) label.Classes.Add(style);
        return label;
    }
    private static Image Logo(double size)
    {
        using var source = AssetLoader.Open(new Uri("avares://Krosforge/Assets/Krosforge.png"));
        return new Image { Source = new Bitmap(source), Width = size, Height = size };
    }
    private static Border Card(Control child) => new() { Child = child, Classes = { "card" }, VerticalAlignment = VerticalAlignment.Top };
    private static void Place(Grid parent, Control child, int row, int column)
    {
        Grid.SetRow(child, row);
        Grid.SetColumn(child, column);
        parent.Children.Add(child);
    }
    private Button Action(string text, Func<Task> action, string? style = null)
    {
        var button = new Button { Content = text };
        if (style != null) button.Classes.Add(style);
        button.Click += async (_, _) => await Run(action);
        return button;
    }
    private void ShowError(string message)
    {
        lastError = message;
        status.Text = Text.Error(message);
        status.IsVisible = true;
    }
    private void UpdateSelection()
    {
        selection.Text = selected.Count == 0 ? Text.Get("SelectProfiles") : selected.Count == 1 ? Text.Get("SelectedSingle") : string.Format(Text.Get("SelectedCount"), selected.Count);
        play.IsEnabled = selected.Count > 0 && hasInstallation && !busy && !closing;
    }
    private async Task Run(Func<Task> action)
    {
        if (busy || closing) return;
        busy = true;
        shell.IsEnabled = false;
        status.IsVisible = false;
        try { await action(); }
        catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException or TimeoutException or OperationCanceledException)
        { ShowError(e is OperationCanceledException or TimeoutException ? "Timeout" : e.Message); }
        finally { busy = false; shell.IsEnabled = !closing; UpdateSelection(); }
    }
    private async Task CreateProfile()
    {
        var name = await Prompt(Text.Get("CreateProfile"), Text.Get("ChooseName"), "", Text.Get("Create"));
        if (name is null) return;
        var profile = await runtime.CallAsync<Profile>("create", value: name);
        selected.Add(profile.Id);
        await Refresh();
    }
    private async Task Refresh()
    {
        lastSnapshot = await runtime.CallAsync<Snapshot>("snapshot");
        RenderSnapshot(lastSnapshot);
    }
    private void RenderSnapshot(Snapshot snapshot)
    {
        hasInstallation = snapshot.Installation != null;
        installation.Text = snapshot.Installation is { } current ? current.Path : Text.Get("SelectInstallation");
        ToolTip.SetTip(installation, snapshot.Installation?.Path);
        installationBuild.Text = snapshot.Installation is { } build ? Text.Get("BuildPrefix") + build.Build : "";
        installationBuild.IsVisible = hasInstallation;
        installationState.Text = hasInstallation ? Text.Get("ClientFound") : Text.Get("ClientMissing");
        installationState.Foreground = Brush.Parse(hasInstallation ? "#75E2BA" : "#EBAA58");
        profileCount.Text = string.Format(Text.Get("ProfilesCount"), snapshot.Profiles.Length);
        selected.IntersectWith(snapshot.Profiles.Select(p => p.Id));
        if (firstRefresh && snapshot.Profiles.Length > 0) selected.Add(snapshot.Profiles[0].Id);
        firstRefresh = false;
        profiles.Children.Clear();
        if (snapshot.Profiles.Length == 0)
        {
            var empty = new StackPanel { Spacing = 16, Margin = new Thickness(24, 28), HorizontalAlignment = HorizontalAlignment.Center };
            empty.Children.Add(Logo(96));
            empty.Children.Add(new TextBlock { Text = Text.Get("NoProfiles"), FontSize = 20, FontWeight = FontWeight.SemiBold, TextAlignment = TextAlignment.Center });
            empty.Children.Add(new TextBlock { Text = Text.Get("EmptyHelp"), Classes = { "muted" }, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 320 });
            profiles.Children.Add(Card(empty));
        }
        foreach (var profile in snapshot.Profiles)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var name = new TextBlock { Text = profile.Name, FontSize = 16, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, MaxLines = 1, MaxWidth = 260 };
            ToolTip.SetTip(name, profile.Name);
            var info = new StackPanel { Spacing = 4 };
            info.Children.Add(name);
            info.Children.Add(new TextBlock { Text = Text.Get("LocalProfile"), Classes = { "muted" }, FontSize = 12 });
            var check = new CheckBox { Content = info, IsChecked = selected.Contains(profile.Id), VerticalAlignment = VerticalAlignment.Center };
            Avalonia.Automation.AutomationProperties.SetName(check, profile.Name);
            row.Children.Add(check);
            var menu = new ContextMenu();
            var rename = new MenuItem { Header = Text.Get("Rename") };
            rename.Click += async (_, _) => await Run(async () =>
            {
                var value = await Prompt(Text.Get("RenameProfile"), Text.Get("NewName"), profile.Name, Text.Get("Save"));
                if (value != null) { await runtime.CallAsync<bool>("rename", profile.Id, value); await Refresh(); }
            });
            var delete = new MenuItem { Header = Text.Get("Delete") };
            delete.Click += async (_, _) => await Run(async () =>
            {
                var confirmation = await Prompt(Text.Get("DeleteProfile"), string.Format(Text.Get("DeleteConfirmation"), profile.Name), "", Text.Get("Delete"));
                if (confirmation != null) { await runtime.CallAsync<bool>("delete", profile.Id, confirmation: confirmation); await Refresh(); }
            });
            menu.Items.Add(rename);
            menu.Items.Add(delete);
            var options = new Button { Content = "•••", Classes = { "subtle" }, ContextMenu = menu, VerticalAlignment = VerticalAlignment.Center };
            Avalonia.Automation.AutomationProperties.SetName(options, string.Format(Text.Get("ProfileOptions"), profile.Name));
            ToolTip.SetTip(options, Text.Get("ProfileActions"));
            options.Click += (_, _) => menu.Open(options);
            Place(row, options, 0, 1);
            var card = Card(row);
            card.Classes.Add("profile");
            card.Classes.Set("selected", check.IsChecked == true);
            check.IsCheckedChanged += (_, _) =>
            {
                if (check.IsChecked == true) selected.Add(profile.Id); else selected.Remove(profile.Id);
                card.Classes.Set("selected", check.IsChecked == true);
                UpdateSelection();
            };
            profiles.Children.Add(card);
        }
        UpdateSelection();
    }
    private async Task ExportLicenses()
    {
        using var source = typeof(App).Assembly.GetManifestResourceStream("Krosforge.Licenses.zip")
            ?? throw new IOException("LicensesRepository");
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Text.Get("SaveLicenses"), SuggestedFileName = "Krosforge-licenses.zip", DefaultExtension = "zip"
        });
        if (file is null) return;
        await using var target = await file.OpenWriteAsync();
        await source.CopyToAsync(target);
    }
    private async Task ChooseInstallation()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = Text.Get("SelectClient"), AllowMultiple = false });
        if (folders.Count == 0) return;
        var path = folders[0].TryGetLocalPath() ?? throw new IOException("SelectLocalFolder");
        await runtime.CallAsync<Installation>("installation", value: path);
        await Refresh();
    }
    private async Task<string?> Prompt(string title, string message, string? value, string accept)
    {
        var dialog = new Window { Title = title, Icon = Icon, Width = 460, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Spacing = 18, Margin = new Thickness(28) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 22, FontWeight = FontWeight.SemiBold });
        panel.Children.Add(Label(message.Replace("\\n", "\n"), "muted"));
        var input = new TextBox { Text = value, MaxLength = 40 };
        Avalonia.Automation.AutomationProperties.SetName(input, title);
        if (value != null) panel.Children.Add(input);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Right };
        if (value != null)
        {
            var cancel = new Button { Content = Text.Get("Cancel"), IsCancel = true };
            cancel.Click += (_, _) => dialog.Close((string?)null);
            buttons.Children.Add(cancel);
        }
        var ok = new Button { Content = accept, IsDefault = true, Classes = { value != null && accept == Text.Get("Delete") ? "danger" : "primary" } };
        ok.Click += (_, _) => dialog.Close(value == null ? "" : input.Text ?? "");
        buttons.Children.Add(ok);
        panel.Children.Add(buttons);
        dialog.Content = panel;
        dialog.Opened += (_, _) => { if (value != null) { input.Focus(); input.SelectAll(); } };
        return await dialog.ShowDialog<string?>(this);
    }
}
