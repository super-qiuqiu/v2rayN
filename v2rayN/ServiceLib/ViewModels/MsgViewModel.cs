namespace ServiceLib.ViewModels;

public class MsgViewModel : MyReactiveObject, IDisposable
{
    private static readonly TimeSpan MsgBufferInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ScrollInterval = TimeSpan.FromSeconds(2);
    private readonly ConcurrentQueue<string> _queueMsg = new();
    private readonly CompositeDisposable _disposables = [];
    private volatile bool _lastMsgFilterNotAvailable;
    private int _pendingScroll;
    public int NumMaxMsg { get; } = 500;

    [Reactive]
    public string MsgFilter { get; set; }

    [Reactive]
    public bool AutoRefresh { get; set; }

    public MsgViewModel(Func<EViewAction, object?, Task<bool>>? updateView)
    {
        _config = AppManager.Instance.Config;
        _updateView = updateView;
        MsgFilter = _config.MsgUIItem.MainMsgFilter ?? string.Empty;
        AutoRefresh = _config.MsgUIItem.AutoRefresh ?? true;

        this.WhenAnyValue(
           x => x.MsgFilter)
               .Subscribe(c => DoMsgFilter());

        this.WhenAnyValue(
          x => x.AutoRefresh,
          y => y == true)
              .Subscribe(c => _config.MsgUIItem.AutoRefresh = AutoRefresh);

        _disposables.Add(AppEvents.SendMsgViewRequested
         .AsObservable()
         .Buffer(MsgBufferInterval, 100)
         .Where(messages => messages.Count > 0)
         .Subscribe(messages => _ = AppendQueueMsg(messages)));

        _disposables.Add(Observable.Interval(ScrollInterval)
         .Subscribe(tick => _ = ScrollToEnd()));
    }

    private async Task AppendQueueMsg(IList<string> messages)
    {
        if (AutoRefresh == false)
        {
            return;
        }

        foreach (var msg in messages)
        {
            EnqueueQueueMsg(msg);
        }

        if (!AppManager.Instance.ShowInTaskbar)
        {
            return;
        }

        var sb = new StringBuilder();
        while (_queueMsg.TryDequeue(out var line))
        {
            sb.Append(line);
        }

        if (sb.Length <= 0)
        {
            return;
        }

        Interlocked.Exchange(ref _pendingScroll, 1);
        try
        {
            await _updateView?.Invoke(EViewAction.DispatcherShowMsg, sb.ToString());
        }
        catch (Exception ex)
        {
            Logging.SaveLog(nameof(MsgViewModel), ex);
        }
    }

    private async Task ScrollToEnd()
    {
        if (Interlocked.Exchange(ref _pendingScroll, 0) == 0)
        {
            return;
        }
        if (AutoRefresh == false || !AppManager.Instance.ShowInTaskbar)
        {
            return;
        }

        try
        {
            await _updateView?.Invoke(EViewAction.DispatcherScrollToEnd, null);
        }
        catch (Exception ex)
        {
            Logging.SaveLog(nameof(MsgViewModel), ex);
        }
    }

    private void EnqueueQueueMsg(string msg)
    {
        //filter msg
        if (MsgFilter.IsNotEmpty() && !_lastMsgFilterNotAvailable)
        {
            try
            {
                if (!Regex.IsMatch(msg, MsgFilter))
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                EnqueueWithLimit(ex.Message);
                _lastMsgFilterNotAvailable = true;
            }
        }

        EnqueueWithLimit(msg);
        if (!msg.EndsWith(Environment.NewLine))
        {
            EnqueueWithLimit(Environment.NewLine);
        }
    }

    private void EnqueueWithLimit(string item)
    {
        _queueMsg.Enqueue(item);

        while (_queueMsg.Count > NumMaxMsg)
        {
            _queueMsg.TryDequeue(out _);
        }
    }

    //public void ClearMsg()
    //{
    //    _queueMsg.Clear();
    //}

    private void DoMsgFilter()
    {
        _config.MsgUIItem.MainMsgFilter = MsgFilter;
        _lastMsgFilterNotAvailable = false;
    }

    public void Dispose()
    {
        _disposables.Dispose();
    }
}
