using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace SBT2;

// ---------- Пути и встроенные файлы ----------
public static class AppPaths
{
    public static readonly string Data = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StalzoneServerSelector");
    public static string Bin => Path.Combine(Data, "bin");
    static AppPaths() { Directory.CreateDirectory(Data); }
}

public static class Embedded
{
    static Stream? Open(string name) => Assembly.GetExecutingAssembly().GetManifestResourceStream(name);

    public static string? ReadText(string name)
    {
        using var s = Open(name);
        if (s == null) return null;
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    }

    public static void Extract(string name, string path)
    {
        using var s = Open(name) ?? throw new FileNotFoundException("Нет встроенного файла " + name);
        if (File.Exists(path) && new FileInfo(path).Length == s.Length) return;
        using var f = File.Create(path);
        s.CopyTo(f);
    }
}

// ---------- Список серверов ----------
public static class ServerList
{
    const string Url = "https://raw.githubusercontent.com/RealAngles/Stalzone-Server-Blocker/refs/heads/main/Servers.json";
    static string CacheFile => Path.Combine(AppPaths.Data, "Servers.json");

    public static async Task<(List<ServerItem> items, string source)> LoadAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            var json = await http.GetStringAsync(Url);
            var list = ParseJson(json);
            if (list.Count > 0) { File.WriteAllText(CacheFile, json); return (list, "онлайн"); }
        }
        catch { }
        try
        {
            var list = ParseJson(File.ReadAllText(CacheFile));
            if (list.Count > 0) return (list, "кэш");
        }
        catch { }
        try
        {
            var list = ParseJson(Embedded.ReadText("Servers.fallback.json") ?? "[]");
            if (list.Count > 0) return (list, "встроенный список");
        }
        catch { }
        return (new(), "нет данных");
    }

    static List<ServerItem> ParseJson(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var seen = new HashSet<string>();
        return Parse(doc.RootElement).Where(s => seen.Add(s.Key)).ToList();
    }

    // Формат: { "pools": [ { "name": "MSK1", "tunnels": [ { "name": "MSK1-1", "address": "ip:port" } ] } ] }
    static IEnumerable<ServerItem> Parse(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("pools", out var pools) && pools.ValueKind == JsonValueKind.Array)
        {
            foreach (var pool in pools.EnumerateArray())
            {
                if (pool.ValueKind != JsonValueKind.Object) continue;
                var poolName = Str(pool, "name") ?? "";
                if (pool.TryGetProperty("tunnels", out var tunnels) && tunnels.ValueKind == JsonValueKind.Array)
                    foreach (var t in tunnels.EnumerateArray()) { var s = From(t, null, poolName); if (s != null) yield return s; }
            }
            yield break;
        }
        if (e.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in e.EnumerateArray()) { var s = From(x, null); if (s != null) yield return s; }
        }
    }

    static string? Str(JsonElement o, params string[] keys)
    {
        foreach (var p in o.EnumerateObject())
            if (keys.Any(k => k.Equals(p.Name, StringComparison.OrdinalIgnoreCase)))
                return p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.ToString();
        return null;
    }

    static ServerItem? From(JsonElement x, string? key, string pool = "")
    {
        string? name = key, addr = null;
        if (x.ValueKind == JsonValueKind.String) addr = x.GetString();
        else if (x.ValueKind == JsonValueKind.Object)
        {
            name = Str(x, "name", "title", "id") ?? key;
            addr = Str(x, "address", "ip", "host", "addr");
        }
        if (string.IsNullOrWhiteSpace(addr)) return null;
        var parts = addr.Split(':');
        if (!IPAddress.TryParse(parts[0], out _)) return null;
        int port = 29450;
        if (parts.Length > 1) int.TryParse(parts[1], out port);
        return new ServerItem { Name = string.IsNullOrWhiteSpace(name) ? parts[0] : name!, Ip = parts[0], Port = port, Pool = pool };
    }
}

// ---------- Сохранение выбора ----------
public static class SettingsStore
{
    static string FilePath => Path.Combine(AppPaths.Data, "Settings.json");
    record Data(List<string> Allowed);

    public static HashSet<string>? Load()
    {
        try { return JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath))?.Allowed.ToHashSet(); } catch { return null; }
    }
    public static void Save(IEnumerable<string> allowed)
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(new Data(allowed.ToList()))); } catch { }
    }
}

// ---------- Пинг ----------
public sealed record PingStats(long? Median, long Min, long Max, double Jitter, double Loss, int Sent, int Received, string Method);

public static class Pinger
{
    public static async Task<PingStats> MeasureAsync(string ip, int port, int count = 10)
    {
        var rtts = new List<long>();
        try
        {
            using var p = new Ping();
            for (int i = 0; i < count; i++)
            {
                try
                {
                    var r = await p.SendPingAsync(ip, 1000).ConfigureAwait(false);
                    if (r.Status == IPStatus.Success) rtts.Add(Math.Max(r.RoundtripTime, 1));
                }
                catch { }
                await Task.Delay(80).ConfigureAwait(false);
            }
        }
        catch { }
        if (rtts.Count > 0) return Make(rtts, count, "ICMP");

        rtts.Clear(); // ICMP закрыт: меряем время TCP-рукопожатия
        const int tcpCount = 5;
        for (int i = 0; i < tcpCount; i++)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                using var c = new TcpClient();
                var t = c.ConnectAsync(ip, port);
                var done = await Task.WhenAny(t, Task.Delay(1500)).ConfigureAwait(false);
                if (done == t)
                {
                    try { await t.ConfigureAwait(false); }
                    catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionRefused) { }
                    rtts.Add(Math.Max(sw.ElapsedMilliseconds, 1));
                }
            }
            catch { }
            await Task.Delay(80).ConfigureAwait(false);
        }
        if (rtts.Count > 0) return Make(rtts, tcpCount, "TCP");
        return new PingStats(null, 0, 0, 0, 100, tcpCount, 0, "—");
    }

    static PingStats Make(List<long> r, int total, string method)
    {
        var sorted = r.OrderBy(x => x).ToList();
        int n = sorted.Count;
        double med = n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0;
        double jitter = n > 1 ? Enumerable.Range(1, n - 1).Average(i => Math.Abs(r[i] - r[i - 1])) : 0;
        return new PingStats((long)Math.Round(med), sorted[0], sorted[^1], jitter, 100.0 * (total - n) / total, total, n, method);
    }
}

// ---------- Блокировка через WinDivert (драйвер встроен в exe) ----------
public sealed class Blocker
{
    static class Native
    {
        [DllImport("WinDivert64.dll", EntryPoint = "WinDivertOpen", SetLastError = true, CharSet = CharSet.Ansi)]
        public static extern IntPtr Open([MarshalAs(UnmanagedType.LPStr)] string filter, int layer, short priority, ulong flags);
        [DllImport("WinDivert64.dll", EntryPoint = "WinDivertRecv", SetLastError = true)]
        public static extern bool Recv(IntPtr h, byte[] pkt, uint len, out uint recvLen, IntPtr addr);
        [DllImport("WinDivert64.dll", EntryPoint = "WinDivertShutdown", SetLastError = true)]
        public static extern bool Shutdown(IntPtr h, int how);
        [DllImport("WinDivert64.dll", EntryPoint = "WinDivertClose", SetLastError = true)]
        public static extern bool Close(IntPtr h);
    }

    static IntPtr _lib;

    // Распаковывает dll и sys из exe в %LocalAppData% и подключает dll
    static string? InitDriver()
    {
        if (_lib != IntPtr.Zero) return null;
        try
        {
            Directory.CreateDirectory(AppPaths.Bin);
            var dll = Path.Combine(AppPaths.Bin, "WinDivert64.dll");
            Embedded.Extract("WinDivert64.dll", dll);
            Embedded.Extract("WinDivert64.sys", Path.Combine(AppPaths.Bin, "WinDivert64.sys"));
            _lib = NativeLibrary.Load(dll);
            NativeLibrary.SetDllImportResolver(typeof(Blocker).Assembly,
                (name, asm, path) => name == "WinDivert64.dll" ? _lib : IntPtr.Zero);
            return null;
        }
        catch (Exception e) { return "Не удалось подготовить драйвер: " + e.Message; }
    }

    IntPtr _h = new(-1);
    Thread? _t;
    public bool IsRunning => _t is { IsAlive: true };

    public string? Start(IReadOnlyCollection<string> ips)
    {
        Stop();
        if (ips.Count == 0) return null;
        var init = InitDriver();
        if (init != null) return init;

        var sb = new StringBuilder("outbound and (");
        sb.Append(string.Join(" or ", ips.Select(ip => $"ip.DstAddr == {ip}")));
        sb.Append(") and ((tcp.DstPort >= 29450 and tcp.DstPort <= 29460) or (udp.DstPort >= 29450 and udp.DstPort <= 29460))");

        var h = Native.Open(sb.ToString(), 0, 0, 0);
        if (h == new IntPtr(-1))
        {
            int err = Marshal.GetLastWin32Error();
            return err switch
            {
                5 => "Нужен запуск от имени администратора.",
                577 => "Драйвер WinDivert не прошёл проверку подписи Windows.",
                1275 => "Драйвер заблокирован системой (антивирус или Защитник).",
                _ => $"WinDivertOpen: ошибка {err}."
            };
        }
        _h = h;
        _t = new Thread(() =>
        {
            var buf = new byte[65535];
            while (Native.Recv(h, buf, (uint)buf.Length, out _, IntPtr.Zero)) { } // пакеты не возвращаются в сеть = отбрасываются
        }) { IsBackground = true };
        _t.Start();
        return null;
    }

    public void Stop()
    {
        if (_h != new IntPtr(-1))
        {
            Native.Shutdown(_h, 1);
            _t?.Join(1500);
            Native.Close(_h);
            _h = new IntPtr(-1);
        }
        _t = null;
    }
}
