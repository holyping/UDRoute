using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text;
using UDRoute.Logging;
using UDRoute.Maui.Data;
using UDRoute.Maui.Services;
using UDRoute.Maui.Utils;

namespace UDRoute.Maui.ViewModels;

public partial class LogViewModel : ObservableObject
{
    private readonly MauiLogger _logger;
    private readonly LiteDbContext _db;
    private readonly List<LogEntry> _allEntries = new();

    // 并发无锁缓冲队列与更新标记状态机
    private readonly ConcurrentQueue<LogEntry> _incomingQueue = new();
    private int _isUpdatingUi = 0;             // 0: 空闲, 1: 主线程正在遍历并更新 UI
    private int _hasNewLogsDuringUpdate = 0;   // 0: 更新期间无新日志, 1: 显示新记录期间有新 log 到达
    private volatile bool _isPageActive = false; // 是否当前正处于日志页面

    public EngineService Engine { get; }

    [ObservableProperty]
    private ObservableRangeCollection<LogEntry> _logs = new();

    [ObservableProperty]
    private bool _isLoading = false;

    [ObservableProperty]
    private LogLevel _selectedLogLevel;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _autoScroll = true;

    [ObservableProperty]
    private string _logSummaryText = "共 0 条日志";

    public List<LogLevel> AvailableLogLevels { get; } = new()
    {
        LogLevel.Trace,
        LogLevel.Debug,
        LogLevel.Info,
        LogLevel.Warn,
        LogLevel.Error,
        LogLevel.None
    };

    public event Action? RequestScrollToBottom;

    public LogViewModel(EngineService engine, MauiLogger logger, LiteDbContext db)
    {
        Engine = engine;
        _logger = logger;
        _db = db;

        _selectedLogLevel = _logger.Level;

        // 初始化仅拉取到内存缓冲 _allEntries 中，不提前装载进 UI 集合 Logs
        var snapshot = _logger.GetSnapshot();
        lock (_allEntries)
        {
            _allEntries.AddRange(snapshot);
        }

        _logger.LogAppended += OnLogAppended;
        _logger.LogsCleared += OnLogsCleared;
    }

    /// <summary>
    /// 当用户点击切入“日志”页面时调用：激活前台更新，并执行一次平滑的异步分批载入
    /// </summary>
    public async Task OnPageAppearingAsync()
    {
        _isPageActive = true;
        IsLoading = true;

        try
        {
            // 给 UI 线程 30ms 呼吸时间，确保 TabBar 切换动画顺畅完成，绝无卡顿感
            await Task.Delay(30);

            // 批量排空队列中积压的日志
            while (_incomingQueue.TryDequeue(out var pending))
            {
                lock (_allEntries)
                {
                    _allEntries.Add(pending);
                    if (_allEntries.Count > _logger.MaxCapacity)
                    {
                        _allEntries.RemoveAt(0);
                    }
                }
            }

            ApplyFilter();
        }
        finally
        {
            IsLoading = false;
        }

        if (AutoScroll && Logs.Count > 0)
        {
            // 等待视图布局后再触发滚动，避免 Android 强制测量卡死
            await Task.Delay(60);
            RequestScrollToBottom?.Invoke();
        }
    }

    /// <summary>
    /// 当用户离开“日志”页面时调用：冻结前台 UI 刷新，后台仅累积数据，主线程零负担
    /// </summary>
    public void OnPageDisappearing()
    {
        _isPageActive = false;
    }

    partial void OnSelectedLogLevelChanged(LogLevel value)
    {
        if (_logger.Level != value)
        {
            _logger.ChangeLogLevel(value);
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        ApplyFilter();
    }

    partial void OnAutoScrollChanged(bool value)
    {
        if (value && Logs.Count > 0)
        {
            RequestScrollToBottom?.Invoke();
        }
    }

    /// <summary>
    /// 当底层网络/后台线程产生新 Log 时触发。
    /// 若用户未处于日志页面，只保留在内存历史中，绝不调度主线程，杜绝后台卡顿！
    /// </summary>
    private void OnLogAppended(LogEntry entry)
    {
        _incomingQueue.Enqueue(entry);

        // 如果用户不在 LogPage 前台，只在后台简单排空累积，不调度 UI 主线程！
        if (!_isPageActive)
        {
            if (_incomingQueue.Count > 30)
            {
                Task.Run(() =>
                {
                    while (_incomingQueue.TryDequeue(out var item))
                    {
                        lock (_allEntries)
                        {
                            _allEntries.Add(item);
                            if (_allEntries.Count > _logger.MaxCapacity)
                            {
                                _allEntries.RemoveAt(0);
                            }
                        }
                    }
                });
            }
            return;
        }

        // 页面处于前台时，才触发主线程批量消费
        Interlocked.Exchange(ref _hasNewLogsDuringUpdate, 1);
        if (Interlocked.CompareExchange(ref _isUpdatingUi, 1, 0) == 0)
        {
            MainThread.BeginInvokeOnMainThread(ProcessLogQueueOnMainThread);
        }
    }

    /// <summary>
    /// 在主线程上批量排空队列并刷新 UI。
    /// </summary>
    private void ProcessLogQueueOnMainThread()
    {
        while (true)
        {
            Interlocked.Exchange(ref _hasNewLogsDuringUpdate, 0);

            List<LogEntry> batch = new();
            while (_incomingQueue.TryDequeue(out var log))
            {
                batch.Add(log);
            }

            if (batch.Count > 0)
            {
                string search = SearchText?.Trim() ?? string.Empty;
                bool hasSearch = !string.IsNullOrEmpty(search);

                List<LogEntry> uiBatch = new();

                foreach (var item in batch)
                {
                    lock (_allEntries)
                    {
                        _allEntries.Add(item);
                        if (_allEntries.Count > _logger.MaxCapacity)
                        {
                            _allEntries.RemoveAt(0);
                        }
                    }

                    if (hasSearch && !item.Message.Contains(search, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    uiBatch.Add(item);
                }

                if (uiBatch.Count > 0)
                {
                    Logs.AddRange(uiBatch);
                    // 保持和底层最大容量同步，防止 UI 无限膨胀导致 OOM
                    while (Logs.Count > _logger.MaxCapacity)
                    {
                        Logs.RemoveAt(0);
                    }
                }

                UpdateSummary();

                if (AutoScroll && Logs.Count > 0)
                {
                    RequestScrollToBottom?.Invoke();
                }
            }

            if (!_isPageActive)
            {
                Interlocked.Exchange(ref _isUpdatingUi, 0);
                break;
            }

            if (Volatile.Read(ref _hasNewLogsDuringUpdate) == 1 || !_incomingQueue.IsEmpty)
            {
                continue;
            }

            Interlocked.Exchange(ref _isUpdatingUi, 0);
            if (Volatile.Read(ref _hasNewLogsDuringUpdate) == 1 || !_incomingQueue.IsEmpty)
            {
                if (Interlocked.CompareExchange(ref _isUpdatingUi, 1, 0) == 0)
                {
                    continue;
                }
            }

            break;
        }
    }

    private void OnLogsCleared()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            while (_incomingQueue.TryDequeue(out _)) { }
            lock (_allEntries)
            {
                _allEntries.Clear();
            }
            Logs.Clear();
            UpdateSummary();
        });
    }

    private void ApplyFilter()
    {
        string search = SearchText?.Trim() ?? string.Empty;
        List<LogEntry> filtered;
        lock (_allEntries)
        {
            if (string.IsNullOrEmpty(search))
            {
                filtered = _allEntries;
            }
            else
            {
                filtered = _allEntries
                    .Where(e => e.Message.Contains(search, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
        }

        Logs.ReplaceRange(filtered);
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        int total = _allEntries.Count;
        LogSummaryText = $"显示 {Logs.Count} / 总计 {total} 条";
    }

    [RelayCommand]
    private void ClearLogs()
    {
        _logger.Clear();
    }

    [RelayCommand]
    private async Task CopyLogsAsync()
    {
        List<LogEntry> toCopy;
        lock (_allEntries)
        {
            toCopy = new List<LogEntry>(_allEntries);
        }
        if (toCopy.Count == 0) return;

        var sb = new StringBuilder();
        foreach (var item in toCopy)
        {
            sb.AppendLine(item.ToString());
        }

        await Clipboard.Default.SetTextAsync(sb.ToString());
        if (Application.Current?.Windows.Count > 0 && Application.Current.Windows[0].Page != null)
        {
            await Application.Current.Windows[0].Page!.DisplayAlert("已复制全部日志", $"已将全部 {toCopy.Count} 条完整历史日志复制到系统剪贴板", "确定");
        }
    }

    [RelayCommand]
    private async Task ShowLogDetailsAsync(LogEntry entry)
    {
        if (entry == null) return;
        if (Application.Current?.Windows.Count > 0 && Application.Current.Windows[0].Page != null)
        {
            await Application.Current.Windows[0].Page!.DisplayAlert($"日志详情 ({entry.LevelString})", entry.Message, "关闭");
        }
    }

    [RelayCommand]
    private void ToggleAutoScroll()
    {
        AutoScroll = !AutoScroll;
    }

    [RelayCommand]
    private async Task ToggleAsync()
    {
        if (Engine.IsBusy) return;

        if (!Engine.IsRunning)
        {
            var selected = _db.Scenes.FindOne(s => s.IsSelected) ?? _db.Scenes.FindAll().FirstOrDefault();
            if (selected != null)
            {
                await Engine.StartAsync(selected);
            }
            else
            {
                Log.Warn("未找到任何场境可启动。");
            }
        }
        else
        {
            await Engine.StopAsync();
        }
    }
}
