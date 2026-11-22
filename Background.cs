using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;
using DR = System.Drawing;
using WF = System.Windows.Forms;

namespace StalzoneServerSelector;

// ---------- Иконка в трее ----------
public sealed class Tray : IDisposable
{
    readonly WF.NotifyIcon _ni;
    readonly WF.ToolStripMenuItem _toggle;
    readonly DR.Icon _on, _off;

    public Tray(Action show, Action toggle, Action exit)
    {
        _on = MakeIcon(true);
        _off = MakeIcon(false);
        var menu = new WF.ContextMenuStrip();
        menu.Items.Add("Открыть", null, (_, _) => show());
        _toggle = new WF.ToolStripMenuItem("Включить блокировку", null, (_, _) => toggle());
        menu.Items.Add(_toggle);
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => exit());
        _ni = new WF.NotifyIcon { Icon = _off, Text = "Stalzone Server Selector", ContextMenuStrip = menu, Visible = true };
        _ni.DoubleClick += (_, _) => show();
    }

    public void SetBlocking(bool on)
    {
        _ni.Icon = on ? _on : _off;
        _ni.Text = on ? "Stalzone Server Selector: блокировка активна" : "Stalzone Server Selector: выключено";
        _toggle.Text = on ? "Выключить блокировку" : "Включить блокировку";
    }

    public void Notify(string text) => _ni.ShowBalloonTip(4000, "Stalzone Server Selector", text, WF.ToolTipIcon.Warning);

    // Иконка программы; когда блокировка выключена, значок чёрно-белый
    static DR.Icon MakeIcon(bool on)
    {
        DR.Icon baseIcon;
        try { baseIcon = DR.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? DR.SystemIcons.Application; }
        catch { baseIcon = DR.SystemIcons.Application; }
        using var bmp = baseIcon.ToBitmap();
        if (!on)
            for (int y = 0; y < bmp.Height; y++)
                for (int x = 0; x < bmp.Width; x++)
                {
                    var p = bmp.GetPixel(x, y);
                    int g = (int)(p.R * 0.30 + p.G * 0.59 + p.B * 0.11);
                    bmp.SetPixel(x, y, DR.Color.FromArgb(p.A, g, g, g));
                }
        return DR.Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose() { _ni.Visible = false; _ni.Dispose(); }
}

// ---------- Автозапуск через Планировщик задач ----------
// Задача запускается при входе пользователя с наивысшими правами, поэтому окно UAC не появляется.
public static class Autostart
{
    const string TaskName = "StalzoneServerSelector";

    public static bool IsEnabled() => Run($"/Query /TN \"{TaskName}\"") == 0;

    public static string? Enable(bool hidden)
    {
        try
        {
            var exe = Environment.ProcessPath ?? throw new InvalidOperationException("не удалось определить путь к программе");
            var user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
            var args = hidden ? "<Arguments>--background</Arguments>" : "";
            var xml = $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo><Description>Stalzone Server Selector</Description></RegistrationInfo>
  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId><Delay>PT10S</Delay></LogonTrigger></Triggers>
  <Principals><Principal id=""Author""><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Enabled>true</Enabled>
  </Settings>
  <Actions Context=""Author""><Exec><Command>{SecurityElement.Escape(exe)}</Command>{args}</Exec></Actions>
</Task>";
            var tmp = Path.Combine(AppPaths.Data, "task.xml");
            File.WriteAllText(tmp, xml, Encoding.Unicode);
            int code = Run($"/Create /TN \"{TaskName}\" /XML \"{tmp}\" /F");
            try { File.Delete(tmp); } catch { }
            return code == 0 ? null : $"Не удалось создать задачу автозапуска (код {code}).";
        }
        catch (Exception e) { return "Автозапуск: " + e.Message; }
    }

    public static string? Disable()
    {
        if (!IsEnabled()) return null;
        int code = Run($"/Delete /TN \"{TaskName}\" /F");
        return code == 0 ? null : $"Не удалось удалить задачу автозапуска (код {code}).";
    }

    static int Run(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", args)
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
            p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
            p.WaitForExit(15000);
            return p.ExitCode;
        }
        catch { return -1; }
    }
}
