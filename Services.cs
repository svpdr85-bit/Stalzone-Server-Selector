using System.Buffers.Binary;
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

namespace StalzoneServerSelector;

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
    // clientToTunnelRttWeight из Servers.json (по умолчанию 1.0)
    public static double ClientWeight { get; private set; } = 1.0;
    static string CacheFile => Path.Combine(AppPaths.Data, "Servers.json");

    // Мгновенно: сначала кэш, если его нет — список, встроенный в exe
    public static (List<ServerItem> items, string source) LoadLocal()
    {
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

    // В фоне обновляет кэш с GitHub; новый список подхватится при следующем запуске
    public static async Task RefreshCacheAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            var json = await http.GetStringAsync(Url);
            if (ParseJson(json).Count > 0) File.WriteAllText(CacheFile, json);
        }
        catch { }
    }

    static List<ServerItem> ParseJson(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        if (doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("clientToTunnelRttWeight", out var w)
            && w.ValueKind == JsonValueKind.Number && w.TryGetDouble(out var wd) && wd >= 0)
            ClientWeight = wd;
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
public sealed class AppSettings
{
    public List<string> Allowed { get; set; } = new();   // серверы, на которые разрешено попадать (остальные блокируются)
    public bool AutoStart { get; set; }                  // запускать вместе с Windows
    public bool StartHidden { get; set; } = true;        // при автозапуске скрывать окно (работать в трее)
    public bool AutoApply { get; set; }                  // включать блокировку автоматически при запуске
    public bool CloseToTray { get; set; } = true;        // кнопка закрытия сворачивает в трей
}

public static class SettingsStore
{
    static string FilePath => Path.Combine(AppPaths.Data, "Settings.json");
    static AppSettings? _cur;
    public static AppSettings Current => _cur ??= ReadFile();

    static AppSettings ReadFile()
    {
        try
        {
            var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
            s.Allowed ??= new();
            return s;
        }
        catch { return new(); }
    }

    public static HashSet<string>? Load() => Current.Allowed.Count > 0 ? Current.Allowed.ToHashSet() : null;
    public static void Save(IEnumerable<string> allowed) { Current.Allowed = allowed.ToList(); Save(); }
    public static void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true })); } catch { }
    }
}

// ---------- Пинг ----------
// Client = время до туннеля, Server = туннель -> бэкенд (его сообщает сам туннель)
public sealed record PingStats(long? Median, long Min, long Max, double Jitter, double Loss, int Sent, int Received,
                               string Method, long Client = 0, long Server = 0);

// Правило основного Ping:
//   по умолчанию   Ping = ClientRTT * вес + ServerRTT
//   пул MSK2X      Ping = ClientRTT
// Экспериментальная модель, подобранная по сравнению с HUD игры; внутренний алгоритм игры неизвестен.
public static class PingRules
{
    public static bool ClientOnly(string pool) => pool.Equals("MSK2X", StringComparison.OrdinalIgnoreCase);
}

public static class Pinger
{
    // Локальные порты наших проб. Блокировка пропускает пакеты с этих портов, поэтому замер
    // работает и при включённой блокировке. Игра эти порты не использует (её порты из диапазона 49152+).
    public const int ProbePortFirst = 38000, ProbePortLast = 38063;
    static readonly object PortLock = new();
    static int _nextPort = ProbePortFirst;

    static void BindProbePort(Socket s)
    {
        for (int i = 0; i <= ProbePortLast - ProbePortFirst; i++)
        {
            int p;
            lock (PortLock) { p = _nextPort; _nextPort = _nextPort >= ProbePortLast ? ProbePortFirst : _nextPort + 1; }
            try { s.Bind(new IPEndPoint(IPAddress.Any, p)); return; } catch (SocketException) { }
        }
    }

    static readonly object LogLock = new();
    static bool _logStarted;

    // Основной Ping = ClientRTT * вес + ServerRTT. Вес берётся из Servers.json (clientToTunnelRttWeight, сейчас 1.0)
    public static double ClientWeight => ServerList.ClientWeight;

    public static async Task<PingStats> MeasureAsync(string ip, int port, int count = 10, string pool = "")
    {
        var roxy = await MeasureRoxyAsync(ip, port, count, pool).ConfigureAwait(false);
        return roxy.Median != null ? roxy : await MeasureIcmpAsync(ip, count).ConfigureAwait(false);
    }

    // Протокол туннеля: UDP на порт+1. Отправляем 16 байт UUID, ждём 21 байт:
    // UUID (16) + serverRtt (int32, big endian) + flag (1)
    static async Task<PingStats> MeasureRoxyAsync(string ip, int port, int count, string pool)
    {
        var client = new List<double>(); var server = new List<double>(); var combined = new List<double>();
        int flag = -1;
        try
        {
            using var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            BindProbePort(sock);
            sock.Connect(IPAddress.Parse(ip), port + 1);
            var buf = new byte[64];
            for (int i = 0; i < count; i++)
            {
                var id = Guid.NewGuid().ToByteArray();
                using var cts = new CancellationTokenSource(1000);
                try
                {
                    var sw = Stopwatch.StartNew();
                    await sock.SendAsync(id, SocketFlags.None, cts.Token).ConfigureAwait(false);
                    while (true)
                    {
                        int n = await sock.ReceiveAsync(buf, SocketFlags.None, cts.Token).ConfigureAwait(false);
                        double c = sw.Elapsed.TotalMilliseconds;
                        if (n != 21 || !buf.AsSpan(0, 16).SequenceEqual(id)) continue; // чужой или запоздавший пакет
                        int srv = BinaryPrimitives.ReadInt32BigEndian(buf.AsSpan(16, 4));
                        if (srv < 0 || srv > 10000) break;                              // испорченный ответ
                        client.Add(c); server.Add(srv); combined.Add(c * ClientWeight + srv);
                        flag = buf[20];
                        break;
                    }
                }
                catch (OperationCanceledException) { }
                catch (SocketException) { }
                await Task.Delay(50).ConfigureAwait(false);
            }
        }
        catch { }

        if (combined.Count == 0)
        {
            Log($"ROXY SUMMARY {ip}:{port + 1} sent={count} recv=0");
            return new PingStats(null, 0, 0, 0, 100, count, 0, "—");
        }
        var st = Make(PingRules.ClientOnly(pool) ? client : combined, count, "Roxy UDP", Med(client), Med(server));
        Log($"ROXY SUMMARY {ip}:{port + 1} sent={count} recv={combined.Count} client={st.Client} server={st.Server} ping={st.Median} jitter={st.Jitter:0.0} flag={flag} weight={ClientWeight}");
        return st;
    }

    // Запасной вариант, если туннель не ответил по UDP: только время до туннеля
    static async Task<PingStats> MeasureIcmpAsync(string ip, int count)
    {
        var rtts = new List<double>();
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
        return rtts.Count > 0 ? Make(rtts, count, "ICMP") : new PingStats(null, 0, 0, 0, 100, count, 0, "—");
    }

    static long Med(List<double> v)
    {
        var a = v.OrderBy(x => x).ToList();
        return (long)Math.Round(a.Count % 2 == 1 ? a[a.Count / 2] : (a[a.Count / 2 - 1] + a[a.Count / 2]) / 2.0);
    }

    static PingStats Make(List<double> r, int total, string method, long client = 0, long server = 0)
    {
        var sorted = r.OrderBy(x => x).ToList();
        int n = sorted.Count;
        double med = n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0;
        double jitter = n > 1 ? Enumerable.Range(1, n - 1).Average(i => Math.Abs(r[i] - r[i - 1])) : 0;
        return new PingStats((long)Math.Round(med), (long)Math.Round(sorted[0]), (long)Math.Round(sorted[^1]),
                             jitter, 100.0 * (total - n) / total, total, n, method, client, server);
    }

    static void Log(string text)
    {
        try
        {
            lock (LogLock)
            {
                var p = Path.Combine(AppPaths.Data, "RoxyDebug.txt");
                if (!_logStarted) { File.WriteAllText(p, ""); _logStarted = true; }
                File.AppendAllText(p, $"[{DateTime.Now:HH:mm:ss}] {text}{Environment.NewLine}");
            }
        }
        catch { }
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
        // UDP с локальных портов наших проб не блокируем, чтобы можно было замерять и заблокированные серверы
        sb.Append($") and ((tcp.DstPort >= 29450 and tcp.DstPort <= 29460) or (udp.DstPort >= 29450 and udp.DstPort <= 29460 and (udp.SrcPort < {Pinger.ProbePortFirst} or udp.SrcPort > {Pinger.ProbePortLast})))");

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
