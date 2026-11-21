using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace SBT2;

public partial class MainWindow : Window
{
    readonly ObservableCollection<ServerItem> _servers = new();
    readonly Blocker _blocker = new();
    readonly Dictionary<string, (Button btn, string title)> _hdr;
    HashSet<string>? _saved;
    bool _bulk, _busy;
    string? _pool;
    string _sortKey = "score";
    bool _sortAsc = true;

    public MainWindow()
    {
        InitializeComponent();
        List.ItemsSource = _servers;
        _hdr = new()
        {
            ["pool"] = (HPool, "Регион"), ["name"] = (HName, "Сервер"), ["ping"] = (HPing, "Пинг"),
            ["jitter"] = (HJitter, "Джиттер"), ["loss"] = (HLoss, "Потери"), ["score"] = (HScore, "Оценка"),
        };
        RefreshHeaders();
        StateChanged += (_, _) =>
        {
            bool max = WindowState == WindowState.Maximized;
            Root.Margin = max ? SystemParameters.WindowResizeBorderThickness : new Thickness(0);
            MaxBtn.Content = max ? "\uE923" : "\uE922";
        };
        Loaded += async (_, _) =>
        {
            try { await LoadAsync(); await PingAllAsync(); }
            catch (Exception ex)
            {
                Status.Text = "Ошибка: " + ex.Message;
                try { File.WriteAllText(Path.Combine(AppPaths.Data, "crash.log"), ex.ToString()); } catch { }
                Tools.IsEnabled = true; List.IsEnabled = true; MainBtn.IsEnabled = true; PoolList.IsEnabled = true; _busy = false;
            }
        };
        Closing += (_, _) => _blocker.Stop();
    }

    // ----- кнопки заголовка -----
    void Min_Click(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Max_Click(object s, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    void Close_Click(object s, RoutedEventArgs e) => Close();

    // ----- загрузка -----
    async Task LoadAsync()
    {
        var (items, source) = ServerList.LoadLocal();
        _ = ServerList.RefreshCacheAsync();
        _saved = SettingsStore.Load();
        foreach (var s in items)
        {
            s.Allow = _saved?.Contains(s.Key) ?? false;
            s.PropertyChanged += OnItemChanged;
            _servers.Add(s);
        }
        var pools = new List<string> { "Все регионы" };
        pools.AddRange(items.Select(s => s.Pool).Where(p => p != "").Distinct());
        PoolList.ItemsSource = pools;
        CollectionViewSource.GetDefaultView(_servers).Filter = o => _pool == null || ((ServerItem)o).Pool == _pool;
        PoolList.SelectedIndex = 0;
        Status.Text = items.Count == 0 ? "Не удалось получить список серверов." : $"Серверов: {items.Count} (источник: {source})";
        UpdateStats();
    }

    void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ServerItem.Allow) || _bulk) return;
        AfterSelectionChanged();
    }

    void AfterSelectionChanged()
    {
        SettingsStore.Save(_servers.Where(s => s.Allow).Select(s => s.Key));
        UpdateStats();
        if (_blocker.IsRunning) Apply();
    }

    void UpdateStats()
    {
        int allowed = _servers.Count(s => s.Allow);
        Stats.Text = $"Разрешено: {allowed}   ·   Заблокировано: {_servers.Count - allowed}";
    }

    // ----- замер -----
    async Task PingAllAsync()
    {
        if (_busy || _servers.Count == 0) return;
        _busy = true;
        Tools.IsEnabled = false; List.IsEnabled = false; MainBtn.IsEnabled = false; PoolList.IsEnabled = false;

        var targets = _servers.Where(s => !_blocker.IsRunning || s.Allow).ToList(); // заблокированные туннели не ответят
        int total = targets.Count, done = 0;
        Status.Text = $"Замер серверов: 0/{total}…";
        using var sem = new SemaphoreSlim(16);
        await Task.WhenAll(targets.Select(async s =>
        {
            await sem.WaitAsync();
            try { s.SetStats(await Pinger.MeasureAsync(s.Ip, s.Port)); }
            finally { sem.Release(); Status.Text = $"Замер серверов: {++done}/{total}…"; }
        }));

        _sortKey = "score"; _sortAsc = true;
        ApplySort(); RefreshHeaders();

        if (_saved == null && _servers.All(s => !s.Allow)) SelectTop(1);
        var best = _servers.Where(s => s.Score != null).OrderBy(s => s.Score).FirstOrDefault();
        Status.Text = best == null
            ? "Ни один сервер не ответил."
            : $"Замер завершён. Лучший: {best.Name} — {best.PingText}, потери {best.LossText}. Выберите серверы.";

        Tools.IsEnabled = true; List.IsEnabled = true; MainBtn.IsEnabled = true; PoolList.IsEnabled = true;
        _busy = false;
    }

    // ----- сортировка -----
    void Header_Click(object s, RoutedEventArgs e)
    {
        var key = (string)((Button)s).Tag;
        if (key == _sortKey) _sortAsc = !_sortAsc; else { _sortKey = key; _sortAsc = true; }
        ApplySort(); RefreshHeaders();
    }

    void ApplySort()
    {
        Func<ServerItem, IComparable> key = _sortKey switch
        {
            "pool" => s => s.Pool,
            "name" => s => s.Name,
            "ping" => s => s.Ping ?? long.MaxValue,
            "jitter" => s => s.Jitter ?? double.MaxValue,
            "loss" => s => s.Loss,
            _ => s => s.Score ?? double.MaxValue,
        };
        var sorted = (_sortAsc ? _servers.OrderBy(key) : _servers.OrderByDescending(key)).ThenBy(s => s.Name).ToList();
        _servers.Clear();
        foreach (var s in sorted) _servers.Add(s);
    }

    void RefreshHeaders()
    {
        foreach (var (k, (btn, title)) in _hdr)
            btn.Content = title + (k == _sortKey ? (_sortAsc ? " ▲" : " ▼") : "");
    }

    // ----- выбор -----
    IEnumerable<ServerItem> Visible() => _servers.Where(s => _pool == null || s.Pool == _pool);

    void PoolList_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        _pool = PoolList.SelectedIndex <= 0 ? null : PoolList.SelectedItem as string;
        CollectionViewSource.GetDefaultView(_servers).Refresh();
    }

    void Row_Click(object sender, MouseButtonEventArgs e)
    {
        for (var d = e.OriginalSource as DependencyObject; d != null && d != sender;
             d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is CheckBox) return; // клик по самому чекбоксу он обработает сам
        if (((FrameworkElement)sender).DataContext is ServerItem item) item.Allow = !item.Allow;
    }

    void SelectTop(int n)
    {
        var best = Visible().Where(s => s.Score != null).OrderBy(s => s.Score).Take(n).ToHashSet();
        _bulk = true;
        foreach (var s in _servers) s.Allow = best.Contains(s);
        _bulk = false;
        AfterSelectionChanged();
    }

    void Best_Click(object s, RoutedEventArgs e) => SelectTop(1);

    void AllRegion_Click(object s, RoutedEventArgs e)
    {
        var vis = Visible().ToHashSet();
        _bulk = true; foreach (var x in _servers) x.Allow = vis.Contains(x); _bulk = false;
        AfterSelectionChanged();
    }

    void None_Click(object s, RoutedEventArgs e)
    {
        _bulk = true; foreach (var x in _servers) x.Allow = false; _bulk = false;
        AfterSelectionChanged();
    }

    // ----- блокировка -----
    void Apply()
    {
        if (_servers.All(s => !s.Allow))
        {
            _blocker.Stop(); SetState(false);
            MessageBox.Show("Отметьте хотя бы один сервер, иначе заблокируются все.", "Stalzone Server Selector");
            return;
        }
        var blocked = _servers.Where(s => !s.Allow).Select(s => s.Ip).Distinct().ToList();
        var err = _blocker.Start(blocked);
        if (err != null) { SetState(false); MessageBox.Show(err, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error); return; }
        SetState(true);
    }

    void SetState(bool on)
    {
        var conv = new BrushConverter();
        Pill.Background = (Brush)conv.ConvertFrom(on ? "#1E3B2B" : "#232A38")!;
        PillText.Foreground = (Brush)conv.ConvertFrom(on ? "#3DDC84" : "#9AA4B8")!;
        PillText.Text = on ? "● Блокировка активна" : "Выключено";
        MainBtn.Content = on ? "■  Остановить" : "▶  Применить";
        MainBtn.Background = (Brush)conv.ConvertFrom(on ? "#E5484D" : "#2FBF71")!;
    }

    void MainBtn_Click(object s, RoutedEventArgs e)
    {
        if (_blocker.IsRunning) { _blocker.Stop(); SetState(false); _ = PingAllAsync(); } else Apply();
    }
}
