using System.IO;
using System.Windows;

namespace SBT2;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += (s, e) => { Report(e.Exception); e.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (s, e) => Report(e.ExceptionObject as Exception);
    }

    static void Report(Exception? ex)
    {
        var text = ex?.ToString() ?? "неизвестная ошибка";
        var path = Path.Combine(AppPaths.Data, "crash.log");
        try { File.WriteAllText(path, text); } catch { }
        MessageBox.Show(text + "\n\nЛог: " + path, "Ошибка");
    }
}
