using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace StalzoneServerSelector;

public sealed class ServerItem : INotifyPropertyChanged
{
    static readonly Brush Gray = Make("#6B7488"), Green = Make("#3DDC84"), Yellow = Make("#FFC24B"), Red = Make("#FF5C5C");
    static Brush Make(string c) { var b = (SolidColorBrush)new BrushConverter().ConvertFrom(c)!; b.Freeze(); return b; }

    public string Name { get; init; } = "";
    public string Pool { get; init; } = "";
    public string Ip { get; init; } = "";
    public int Port { get; init; } = 29450;
    public string Address => $"{Ip}:{Port}";
    public string Key => Address;

    bool _allow; PingStats? _stats; bool _tested;
    public bool Allow { get => _allow; set { if (_allow == value) return; _allow = value; N(); } }

    // Ping = ClientRTT * вес + ServerRTT (так игра ранжирует туннели). Одно число для показа и сортировки.
    public long? Ping => _stats?.Median;
    public double? Jitter => _stats?.Median is null ? null : _stats.Jitter;
    public double Loss => _stats?.Loss ?? 100;
    // Основной показатель: Ping = ClientRTT * вес + ServerRTT (меньше = лучше). Потери и джиттер показываются отдельно.
    public double? Score => Ping;

    public void SetStats(PingStats s) { _stats = s; _tested = true; N(string.Empty); }

    string Dash => _tested ? "—" : "";
    public string PingText => !_tested ? "…" : Ping is null ? "нет ответа" : $"{Ping} мс";
    public string MinMaxText => _stats?.Median is null ? Dash : $"{_stats.Min}–{_stats.Max} мс";
    public string JitterText => Jitter is null ? Dash : $"{Jitter:0.0} мс";
    public string LossText => !_tested ? "" : $"{Loss:0}%";
    public string LossTip => _stats is null ? "" : $"Получено ответов: {_stats.Received} из {_stats.Sent}"
        + (_stats.Method.StartsWith("Roxy") ? $" · до туннеля {_stats.Client} мс + туннель→сервер {_stats.Server} мс" : "");
    public string PartsText => _stats?.Median is null ? Dash
        : _stats.Method.StartsWith("Roxy") ? (PingRules.ClientOnly(Pool) ? $"{_stats.Client} + ({_stats.Server})" : $"{_stats.Client} + {_stats.Server}") : $"{_stats.Median} (ICMP)";
    public string MethodText => _stats?.Median is null ? Dash : _stats.Method;
    public string ScoreText => Score is null ? Dash : $"{Score:0}";
    public double PingBar => Score is null ? 0 : Math.Max(4, 100 - Math.Min(Score.Value, 250) / 2.5);
    public Brush PingBrush => Score is null ? Gray
        : (_stats!.Loss >= 10 || Score >= 100) ? Red
        : (_stats.Loss > 0 || Score >= 50) ? Yellow : Green;
    public Brush LossBrush => !_tested ? Gray : Loss == 0 ? Green : Loss < 10 ? Yellow : Red;

    public event PropertyChangedEventHandler? PropertyChanged;
    void N([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
