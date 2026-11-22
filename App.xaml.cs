using System.IO;
using System.Windows;

namespace StalzoneServerSelector;

public partial class App : Application
{
    Mutex? _mutex;
    EventWaitHandle? _showEvent;
    MainWindow? _win;
    public Tray? Tray { get; private set; }

    public App()
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;   // окно может быть скрыто в трей, выход только явный
        DispatcherUnhandledException += (s, e) => { Report(e.Exception); e.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (s, e) => Report(e.ExceptionObject as Exception);
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Один экземпляр: повторный запуск просто показывает уже работающее окно
        _mutex = new Mutex(true, @"Local\StalzoneServerSelector_Instance", out bool first);
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\StalzoneServerSelector_Show");
        if (!first) { _showEvent.Set(); Shutdown(); return; }

        _win = new MainWindow();
        MainWindow = _win;
        Tray = new Tray(() => _win.ShowFromTray(), () => _win.ToggleBlock(), () => _win.ExitApp());
        SessionEnding += (_, _) => _win.PrepareExit();   // не мешаем выключению Windows
        new Thread(() =>
        {
            while (_showEvent.WaitOne()) Dispatcher.BeginInvoke(new Action(() => _win.ShowFromTray()));
        }) { IsBackground = true }.Start();

        if (!e.Args.Contains("--background")) _win.Show();   // при автозапуске окно не показывается
        await _win.StartAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Tray?.Dispose();
        base.OnExit(e);
    }

    static void Report(Exception? ex)
    {
        var text = ex?.ToString() ?? "неизвестная ошибка";
        var path = Path.Combine(AppPaths.Data, "crash.log");
        try { File.WriteAllText(path, text); } catch { }
        MessageBox.Show(text + "\n\nЛог: " + path, "Ошибка");
    }
}
