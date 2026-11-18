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

public sealed class ServerWindowTests
{
    [AvaloniaFact]
    public void DialogSavesCancelsAndBlocksActiveSessionsInEveryLanguage()
    {
        var root = Path.Combine(Path.GetTempPath(),"server-window-"+Guid.NewGuid().ToString("N"));
        var connection = new Connection();
        var window = new MainWindow(connection,new LanguagePreferences(root,CultureInfo.GetCultureInfo("en")));
        window.Show();
        try
        {
            foreach(var language in LanguagePreferences.SupportedLanguages)
            {
                window.ChangeLanguage(language);
                Click(window,"ConfigureServer");
                var dialog = Assert.Single(window.OwnedWindows);
                Assert.Equal(Text.Get("ConfigureServer"),dialog.Title);
                Assert.Single(dialog.GetVisualDescendants().OfType<TextBox>()).Text="São João "+language;
                Click(dialog,"SaveServer");
                Assert.Equal("São João "+language,connection.Name);
                Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(),x=>x.Text==connection.Name);
                Click(window,"ConfigureServer"); dialog=Assert.Single(window.OwnedWindows);
                Assert.Single(dialog.GetVisualDescendants().OfType<TextBox>()).Text="Cancelled";
                Click(dialog,"Cancel"); Assert.Equal("São João "+language,connection.Name);
                connection.Active=true;
                Click(window,"ConfigureServer"); dialog=Assert.Single(window.OwnedWindows);
                Assert.False(dialog.GetVisualDescendants().OfType<Button>().Single(x=>Equals(x.Content,Text.Get("SaveServer"))).IsEnabled);
                Click(dialog,"Cancel"); connection.Active=false;
            }
            Assert.Equal(5,connection.Saves);
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); if(Directory.Exists(root)) Directory.Delete(root,true); }
    }
    private static void Click(Window window,string key)
    {
        window.GetVisualDescendants().OfType<Button>().Single(x=>Equals(x.Content,Text.Get(key))).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }
    private sealed class Connection : IRuntimeConnection
    {
        internal string Name="Krosforge";
        internal bool Active;
        internal int Saves;
        public Task<T> CallAsync<T>(string operation,Guid? id=null,string? value=null,string? confirmation=null)
        {
            object result=true;
            if(operation=="snapshot") result=new Snapshot([],null,Active,new(Name));
            if(operation=="configure-server") { Name=value!; Saves++; result=new ServerSettings(Name); }
            return Task.FromResult((T)result);
        }
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}
