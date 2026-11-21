using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SBT2;

// ---------- Пути и встроенные файлы ----------
public static class AppPaths
{
    public static readonly string Data = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "StalzoneServerSelector");

    public static string Bin => Path.Combine(Data, "bin");

    static AppPaths()
    {
        Directory.CreateDirectory(Data);
    }
}

public static class Embedded
{
    static Stream? Open(string name) =>
        Assembly.GetExecutingAssembly().GetManifestResourceStream(name);

    public static string? ReadText(string name)
    {
        using var s = Open(name);
        if (s == null) return null;

        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    }

    public static void Extract(string name, string path)
    {
        using var s = Open(name) ??
            throw new FileNotFoundException("Нет встроенного файла " + name);

        if (File.Exists(path) &&
            new FileInfo(path).Length == s.Length)
            return;

        using var f = File.Create(path);
        s.CopyTo(f);
    }
}

// ---------- Список серверов ----------
public static class ServerList
{
    const string Url =
        "https://raw.githubusercontent.com/RealAngles/Stalzone-Server-Blocker/refs/heads/main/Servers.json";

    static string CacheFile =>
        Path.Combine(AppPaths.Data, "Servers.json");

    // Синхронно загружает уже сохранённый список серверов.
    // Сначала используется кэш, затем встроенный резервный список.
    public static (List<ServerItem> items, string source) LoadLocal()
    {
        try
        {
            var list = ParseJson(File.ReadAllText(CacheFile));
            if (list.Count > 0)
                return (list, "кэш");
        }
        catch { }

        try
        {
            var list = ParseJson(
                Embedded.ReadText("Servers.fallback.json") ?? "[]");

            if (list.Count > 0)
                return (list, "встроенный список");
        }
        catch { }

        return (new List<ServerItem>(), "нет данных");
    }

    // Обновляет локальный кэш из community blocker.
    // Если интернет или сервер недоступен, возвращает локальный список.
    public static async Task<(List<ServerItem> items, string source)> RefreshCacheAsync()
    {
        try
        {
            using var http = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(8)
            };

            var json = await http.GetStringAsync(Url).ConfigureAwait(false);
            var list = ParseJson(json);

            if (list.Count > 0)
            {
                await File.WriteAllTextAsync(CacheFile, json)
                    .ConfigureAwait(false);

                return (list, "онлайн");
            }
        }
        catch { }

        return LoadLocal();
    }

    // Сохраняет прежний публичный метод для совместимости с другими вызовами.
    public static Task<(List<ServerItem> items, string source)> LoadAsync() =>
        RefreshCacheAsync();

    static IEnumerable<ServerItem> Parse(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Object &&
            e.TryGetProperty("pools", out var pools) &&
            pools.ValueKind == JsonValueKind.Array)
        {
            foreach (var pool in pools.EnumerateArray())
            {
                if (pool.ValueKind != JsonValueKind.Object)
                    continue;

                var poolName = Str(pool, "name") ?? "";

                if (pool.TryGetProperty("tunnels", out var tunnels) &&
                    tunnels.ValueKind == JsonValueKind.Array)
                {
                    foreach (var t in tunnels.EnumerateArray())
                    {
                        var s = From(t, null, poolName);
                        if (s != null) yield return s;
                    }
                }
            }

            yield break;
        }

        if (e.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in e.EnumerateArray())
            {
                var s = From(x, null);
                if (s != null) yield return s;
            }
        }
    }

    static string? Str(JsonElement o, params string[] keys)
    {
        foreach (var p in o.EnumerateObject())
        {
            if (keys.Any(k =>
                k.Equals(p.Name, StringComparison.OrdinalIgnoreCase)))
            {
                return p.Value.ValueKind == JsonValueKind.String
                    ? p.Value.GetString()
                    : p.Value.ToString();
            }
        }

        return null;
    }

    static ServerItem? From(
        JsonElement x,
        string? key,
        string pool = "")
    {
        string? name = key;
        string? addr = null;

        if (x.ValueKind == JsonValueKind.String)
        {
            addr = x.GetString();
        }
        else if (x.ValueKind == JsonValueKind.Object)
        {
            name = Str(x, "name", "title", "id") ?? key;
            addr = Str(x, "address", "ip", "host", "addr");
        }

        if (string.IsNullOrWhiteSpace(addr))
            return null;

        var parts = addr.Split(':');

        if (parts.Length > 2 ||
            !IPAddress.TryParse(parts[0], out _))
            return null;

        int port = 29450;

        if (parts.Length > 1 &&
            (!int.TryParse(parts[1], out port) ||
             port < 1 || port >= 65535))
            return null;

        return new ServerItem
        {
            Name = string.IsNullOrWhiteSpace(name)
                ? parts[0]
                : name!,
            Ip = parts[0],
            Port = port,
            Pool = pool
        };
    }

    static List<ServerItem> ParseJson(string json)
    {
        using var doc = JsonDocument.Parse(
            json,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });

        var seen = new HashSet<string>();

        return Parse(doc.RootElement)
            .Where(s => seen.Add(s.Key))
            .ToList();
    }
}

// ---------- Сохранение выбора ----------
public static class SettingsStore
{
    static string FilePath =>
        Path.Combine(AppPaths.Data, "Settings.json");

    record Data(List<string> Allowed);

    public static HashSet<string>? Load()
    {
        try
        {
            return JsonSerializer
                .Deserialize<Data>(File.ReadAllText(FilePath))
                ?.Allowed.ToHashSet();
        }
        catch
        {
            return null;
        }
    }

    public static void Save(IEnumerable<string> allowed)
    {
        try
        {
            File.WriteAllText(
                FilePath,
                JsonSerializer.Serialize(new Data(allowed.ToList())));
        }
        catch { }
    }
}

// ---------- Статистика Ping ----------
// Ping — округлённое среднее game-weighted значение.
// Median оставлен как алиас, чтобы не ломать старые обращения к PingStats.Median.
public sealed record PingStats(
    long? Ping,
    long Min,
    long Max,
    double Jitter,
    double Loss,
    int Sent,
    int Received,
    string Method,
    bool LimitReached = false,
    double ClientAverageMs = 0,
    double ServerAverageMs = 0,
    double ClientWeight = 1.0,
    long ClientMinMs = 0,
    long ClientMaxMs = 0,
    double ClientJitterMs = 0)
{
    public long? Median => Ping;
}

// ---------- Пинг Roxy ----------
public static class Pinger
{
    private const string CatalogUrl =
        "https://backend.stalzone.net/address_list?login=EXBO-Community";

    // Все параллельные измерения используют один запрос каталога.
    // Если backend временно недоступен, используется последнее известное
    // значение веса 1.0; причина fallback записывается в RoxyDebug.txt.
    private static readonly Lazy<Task<double>> ClientWeightTask =
        new(LoadClientWeightAsync, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly object RoxyLogLock = new();

    private readonly record struct ProbeSample(
        double ClientMs,
        double ServerMs,
        bool LimitReached);

    private static void WriteRoxyLog(string message)
    {
        try
        {
            lock (RoxyLogLock)
            {
                File.AppendAllText(
                    Path.Combine(AppPaths.Data, "RoxyDebug.txt"),
                    message + Environment.NewLine);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string F(double value) =>
        value.ToString("0.000", CultureInfo.InvariantCulture);

    private static async Task<double> LoadClientWeightAsync()
    {
        const double fallbackWeight = 1.0;

        try
        {
            using var http = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(6)
            };

            using var response = await http.GetAsync(
                CatalogUrl,
                HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);

            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content
                .ReadAsStreamAsync().ConfigureAwait(false);

            using var doc = await JsonDocument.ParseAsync(stream)
                .ConfigureAwait(false);

            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Каталог Roxy имеет неверный формат.");

            string mode = root.TryGetProperty("mode", out var modeElement) &&
                          modeElement.ValueKind == JsonValueKind.String
                ? modeElement.GetString() ?? "не указан"
                : "не указан";

            if (!root.TryGetProperty(
                    "clientToTunnelRttWeight",
                    out var weightElement) ||
                weightElement.ValueKind != JsonValueKind.Number ||
                !weightElement.TryGetDouble(out double weight) ||
                !double.IsFinite(weight) ||
                weight < 0)
            {
                throw new InvalidDataException(
                    "В каталоге отсутствует корректный clientToTunnelRttWeight.");
            }

            WriteRoxyLog(
                $"ROXY CATALOG: mode={mode}, " +
                $"clientToTunnelRttWeight={F(weight)}");

            return weight;
        }
        catch (Exception ex)
        {
            WriteRoxyLog(
                $"ROXY CATALOG WARNING: {ex.GetType().Name}: {ex.Message}. " +
                $"Using fallback clientToTunnelRttWeight={F(fallbackWeight)}");

            return fallbackWeight;
        }
    }

    public static async Task<PingStats> MeasureAsync(
        string ip,
        int port,
        int count = 10)
    {
        if (!IPAddress.TryParse(ip, out var address) ||
            port < 1 ||
            port >= 65535 ||
            count < 1)
        {
            return new PingStats(
                null, 0, 0, 0, 100,
                Math.Max(count, 0), 0, "Roxy UDP");
        }

        // Вес запрашивается у того же backend, который выдаёт Roxy-каталог.
        double clientWeight = await ClientWeightTask.Value.ConfigureAwait(false);

        var samples = new List<ProbeSample>(count);
        bool lastLimitReached = false;

        for (int i = 0; i < count; i++)
        {
            try
            {
                using var udp = new UdpClient(address.AddressFamily);
                udp.Connect(new IPEndPoint(address, port + 1));

                var token = new byte[16];
                RandomNumberGenerator.Fill(token);

                // UUID v4 bits, как в опубликованной реализации community-probe.
                token[6] = (byte)((token[6] & 0x0F) | 0x40);
                token[8] = (byte)((token[8] & 0x3F) | 0x80);

                using var timeout = new CancellationTokenSource(
                    TimeSpan.FromSeconds(1));

                long started = Stopwatch.GetTimestamp();

                await udp.SendAsync(token, timeout.Token)
                    .ConfigureAwait(false);

                while (true)
                {
                    var result = await udp.ReceiveAsync(timeout.Token)
                        .ConfigureAwait(false);

                    var data = result.Buffer;

                    // Принимаем только ответ нужного размера с нашим UUID.
                    if (data.Length != 21 ||
                        !data.AsSpan(0, 16).SequenceEqual(token))
                    {
                        continue;
                    }

                    int serverRtt =
                        BinaryPrimitives.ReadInt32BigEndian(
                            data.AsSpan(16, 4));

                    if (serverRtt < 0)
                    {
                        WriteRoxyLog(
                            $"ROXY INVALID: {ip}:{port + 1}, " +
                            $"negative serverRtt={serverRtt}");
                        break;
                    }

                    // В community-коде ClientRTT сохраняется в микросекундах.
                    // Не округляем его сразу до целых миллисекунд.
                    double elapsedMicroseconds =
                        Stopwatch.GetElapsedTime(started).TotalMilliseconds * 1000.0;

                    long clientMicroseconds =
                        Math.Max(0L, (long)Math.Truncate(elapsedMicroseconds));

                    double clientMs = clientMicroseconds / 1000.0;
                    double serverMs = serverRtt;

                    // Последний байт — булев LimitReached, а не произвольный flag.
                    bool limitReached = data[20] != 0;
                    lastLimitReached = limitReached;

                    double sampleWeightedPing =
                        clientMs * clientWeight + serverMs;

                    WriteRoxyLog(
                        $"ROXY DEBUG: {ip}:{port + 1}, " +
                        $"clientRtt={F(clientMs)}ms, " +
                        $"serverRtt={F(serverMs)}ms, " +
                        $"weightedPing={F(sampleWeightedPing)}ms, " +
                        $"clientWeight={F(clientWeight)}, " +
                        $"limitReached={limitReached}, " +
                        $"responseLength={data.Length}");

                    samples.Add(new ProbeSample(
                        clientMs,
                        serverMs,
                        limitReached));

                    break;
                }
            }
            catch (OperationCanceledException) { }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }

            await Task.Delay(80).ConfigureAwait(false);
        }

        if (samples.Count == 0)
        {
            WriteRoxyLog(
                $"ROXY SUMMARY: {ip}:{port}, no successful probes, " +
                $"received=0/{count}, clientWeight={F(clientWeight)}");

            return new PingStats(
                null, 0, 0, 0, 100,
                count, 0, "Roxy UDP",
                false, 0, 0, clientWeight);
        }

        PingStats stats = MakeGameWeighted(samples, count, clientWeight);

        WriteRoxyLog(
            $"ROXY SUMMARY: {ip}:{port}, " +
            $"clientAverage={F(stats.ClientAverageMs)}ms, " +
            $"serverAverage={F(stats.ServerAverageMs)}ms, " +
            $"weightedPing={F(stats.ClientAverageMs * clientWeight + stats.ServerAverageMs)}ms, " +
            $"weightedPingRounded={stats.Median}ms, " +
            $"jitter={F(stats.Jitter)}ms, " +
            $"loss={F(stats.Loss)}%, " +
            $"clientWeight={F(clientWeight)}, " +
            $"limitReached={stats.LimitReached}, " +
            $"received={stats.Received}/{stats.Sent}");

        return stats;
    }

    // Повторяет статистику internal/tunnel/stats.go:
    // ClientRTT и ServerRTT усредняются отдельно, Ping = ClientAvg * weight + ServerAvg.
    // Jitter считается как средняя абсолютная разница соседних (Client + Server)
    // без применения веса, а Loss — доля неуспешных проб.
    private static PingStats MakeGameWeighted(
        List<ProbeSample> samples,
        int total,
        double clientWeight)
    {
        int received = samples.Count;

        double clientAverage = samples.Average(s => s.ClientMs);
        double serverAverage = samples.Average(s => s.ServerMs);
        double weightedPing = clientAverage * clientWeight + serverAverage;

        double jitterTotal = 0;
        for (int i = 1; i < samples.Count; i++)
        {
            double previousTotal =
                samples[i - 1].ClientMs + samples[i - 1].ServerMs;
            double currentTotal =
                samples[i].ClientMs + samples[i].ServerMs;

            jitterTotal += Math.Abs(currentTotal - previousTotal);
        }

        double jitter = samples.Count > 1
            ? jitterTotal / (samples.Count - 1)
            : 0;

        var weightedSamples = samples
            .Select(s => s.ClientMs * clientWeight + s.ServerMs)
            .ToList();

        var clientSamples = samples
            .Select(s => s.ClientMs)
            .ToList();

        long min = RoundMs(weightedSamples.Min());
        long max = RoundMs(weightedSamples.Max());
        long displayedPing = RoundMs(weightedPing);
        long clientMin = RoundMs(clientSamples.Min());
        long clientMax = RoundMs(clientSamples.Max());

        double clientJitter = samples.Count > 1
            ? Enumerable.Range(1, samples.Count - 1)
                .Average(i => Math.Abs(
                    samples[i].ClientMs - samples[i - 1].ClientMs))
            : 0;

        double loss = 100.0 * (total - received) / total;

        return new PingStats(
            displayedPing,
            min,
            max,
            jitter,
            loss,
            total,
            received,
            "Roxy UDP",
            samples[^1].LimitReached,
            clientAverage,
            serverAverage,
            clientWeight,
            clientMin,
            clientMax,
            clientJitter);
    }

    private static long RoundMs(double value) =>
        (long)Math.Round(value, MidpointRounding.AwayFromZero);
}

// ---------- Блокировка через WinDivert ----------
public sealed class Blocker
{
    static class Native
    {
        [DllImport(
            "WinDivert64.dll",
            EntryPoint = "WinDivertOpen",
            SetLastError = true,
            CharSet = CharSet.Ansi)]
        public static extern IntPtr Open(
            [MarshalAs(UnmanagedType.LPStr)] string filter,
            int layer,
            short priority,
            ulong flags);

        [DllImport(
            "WinDivert64.dll",
            EntryPoint = "WinDivertRecv",
            SetLastError = true)]
        public static extern bool Recv(
            IntPtr h,
            byte[] pkt,
            uint len,
            out uint recvLen,
            IntPtr addr);

        [DllImport(
            "WinDivert64.dll",
            EntryPoint = "WinDivertShutdown",
            SetLastError = true)]
        public static extern bool Shutdown(IntPtr h, int how);

        [DllImport(
            "WinDivert64.dll",
            EntryPoint = "WinDivertClose",
            SetLastError = true)]
        public static extern bool Close(IntPtr h);
    }

    static IntPtr _lib;

    static string? InitDriver()
    {
        if (_lib != IntPtr.Zero)
            return null;

        try
        {
            Directory.CreateDirectory(AppPaths.Bin);

            var dll = Path.Combine(
                AppPaths.Bin,
                "WinDivert64.dll");

            Embedded.Extract("WinDivert64.dll", dll);

            Embedded.Extract(
                "WinDivert64.sys",
                Path.Combine(AppPaths.Bin, "WinDivert64.sys"));

            _lib = NativeLibrary.Load(dll);

            NativeLibrary.SetDllImportResolver(
                typeof(Blocker).Assembly,
                (name, asm, path) =>
                    name == "WinDivert64.dll"
                        ? _lib
                        : IntPtr.Zero);

            return null;
        }
        catch (Exception e)
        {
            return "Не удалось подготовить драйвер: " + e.Message;
        }
    }

    IntPtr _h = new(-1);
    Thread? _t;

    public bool IsRunning =>
        _t is { IsAlive: true };

    public string? Start(IReadOnlyCollection<string> ips)
    {
        Stop();

        if (ips.Count == 0)
            return null;

        var init = InitDriver();

        if (init != null)
            return init;

        var sb = new StringBuilder("outbound and (");

        sb.Append(string.Join(
            " or ",
            ips.Select(ip => $"ip.DstAddr == {ip}")));

        sb.Append(
            ") and ((tcp.DstPort >= 29450 and tcp.DstPort <= 29460) or " +
            "(udp.DstPort >= 29450 and udp.DstPort <= 29460))");

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

            while (Native.Recv(
                h,
                buf,
                (uint)buf.Length,
                out _,
                IntPtr.Zero))
            {
                // Пакеты намеренно не возвращаются в сеть.
            }
        })
        {
            IsBackground = true
        };

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
