namespace ServiceLib.Manager;

public class StatisticsManager
{
    private static readonly Lazy<StatisticsManager> instance = new(() => new());
    public static StatisticsManager Instance => instance.Value;

    private Config _config;
    private ServerStatItem? _serverStatItem;
    private List<ServerStatItem> _lstServerStat = [];
    private readonly object _serverStatLock = new();
    private Func<ServerSpeedItem, Task>? _updateFunc;

    private StatisticsXrayService? _statisticsXray;
    private StatisticsSingboxService? _statisticsSingbox;
    private static readonly string _tag = "StatisticsHandler";
    public List<ServerStatItem> ServerStat
    {
        get
        {
            lock (_serverStatLock)
            {
                return [.. _lstServerStat];
            }
        }
    }

    public async Task Init(Config config, Func<ServerSpeedItem, Task> updateFunc)
    {
        _config = config;
        _updateFunc = updateFunc;
        Close();

        if (config.GuiItem.EnableStatistics || _config.GuiItem.DisplayRealTimeSpeed)
        {
            await InitData();

            if (AppManager.Instance.IsRunningCore(ECoreType.Xray))
            {
                _statisticsXray = new StatisticsXrayService(config, UpdateServerStatHandler);
            }
            else if (AppManager.Instance.IsRunningCore(ECoreType.sing_box))
            {
                _statisticsSingbox = new StatisticsSingboxService(config, UpdateServerStatHandler);
            }
        }
    }

    public void Close()
    {
        try
        {
            _statisticsXray?.Close();
            _statisticsSingbox?.Close();
            _statisticsXray = null;
            _statisticsSingbox = null;
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
    }

    public async Task ClearAllServerStatistics()
    {
        await SQLiteHelper.Instance.ExecuteAsync($"delete from ServerStatItem ");
        lock (_serverStatLock)
        {
            _serverStatItem = null;
            _lstServerStat = [];
        }
    }

    public async Task SaveTo()
    {
        try
        {
            var snapshot = ServerStat;
            if (snapshot.Count > 0)
            {
                await SQLiteHelper.Instance.UpdateAllAsync(snapshot);
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
    }

    public async Task CloneServerStatItem(string indexId, string toIndexId)
    {
        if (indexId == toIndexId)
        {
            return;
        }

        ServerStatItem? stat;
        lock (_serverStatLock)
        {
            stat = _lstServerStat.FirstOrDefault(t => t.IndexId == indexId);
        }
        if (stat == null)
        {
            return;
        }

        var toStat = JsonUtils.DeepCopy(stat);
        toStat.IndexId = toIndexId;
        await SQLiteHelper.Instance.ReplaceAsync(toStat);
        lock (_serverStatLock)
        {
            if (_lstServerStat.All(t => t.IndexId != toIndexId))
            {
                _lstServerStat.Add(toStat);
            }
        }
    }

    private async Task InitData()
    {
        await SQLiteHelper.Instance.ExecuteAsync($"delete from ServerStatItem where indexId not in ( select indexId from ProfileItem )");

        var ticks = DateTime.Now.Date.Ticks;
        await SQLiteHelper.Instance.ExecuteAsync($"update ServerStatItem set todayUp = 0,todayDown=0,dateNow={ticks} where dateNow<>{ticks}");

        var serverStats = await SQLiteHelper.Instance.TableAsync<ServerStatItem>().ToListAsync();
        lock (_serverStatLock)
        {
            _serverStatItem = null;
            _lstServerStat = serverStats;
        }
    }

    private async Task UpdateServerStatHandler(ServerSpeedItem server)
    {
        await UpdateServerStat(server);
    }

    private async Task UpdateServerStat(ServerSpeedItem server)
    {
        ServerStatItem? serverStatItem;
        bool created;

        lock (_serverStatLock)
        {
            (serverStatItem, created) = GetServerStatItem(_config.IndexId);
            if (serverStatItem is null)
            {
                return;
            }

            if (server.ProxyUp != 0 || server.ProxyDown != 0)
            {
                serverStatItem.TodayUp += server.ProxyUp;
                serverStatItem.TodayDown += server.ProxyDown;
                serverStatItem.TotalUp += server.ProxyUp;
                serverStatItem.TotalDown += server.ProxyDown;
            }

            server.IndexId = _config.IndexId;
            server.TodayUp = serverStatItem.TodayUp;
            server.TodayDown = serverStatItem.TodayDown;
            server.TotalUp = serverStatItem.TotalUp;
            server.TotalDown = serverStatItem.TotalDown;
        }

        if (created)
        {
            await SQLiteHelper.Instance.ReplaceAsync(serverStatItem);
        }

        await _updateFunc?.Invoke(server);
    }

    private (ServerStatItem? ServerStatItem, bool Created) GetServerStatItem(string indexId)
    {
        var ticks = DateTime.Now.Date.Ticks;
        var created = false;

        lock (_serverStatLock)
        {
            if (_serverStatItem != null && _serverStatItem.IndexId != indexId)
            {
                _serverStatItem = null;
            }

            if (_serverStatItem == null)
            {
                _serverStatItem = _lstServerStat.FirstOrDefault(t => t.IndexId == indexId);
                if (_serverStatItem == null)
                {
                    _serverStatItem = new ServerStatItem
                    {
                        IndexId = indexId,
                        TotalUp = 0,
                        TotalDown = 0,
                        TodayUp = 0,
                        TodayDown = 0,
                        DateNow = ticks
                    };
                    _lstServerStat.Add(_serverStatItem);
                    created = true;
                }
            }

            if (_serverStatItem.DateNow != ticks)
            {
                _serverStatItem.TodayUp = 0;
                _serverStatItem.TodayDown = 0;
                _serverStatItem.DateNow = ticks;
            }

            return (_serverStatItem, created);
        }
    }
}
