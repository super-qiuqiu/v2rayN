using System.Net.WebSockets;

namespace ServiceLib.Services.Statistics;

public class StatisticsSingboxService
{
    private readonly Config _config;
    private bool _exitFlag;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private ClientWebSocket? webSocket;
    private readonly Func<ServerSpeedItem, Task>? _updateFunc;
    private string Url => $"ws://{Global.Loopback}:{AppManager.Instance.StatePort2}/traffic";
    private static readonly string _tag = "StatisticsSingboxService";

    public StatisticsSingboxService(Config config, Func<ServerSpeedItem, Task> updateFunc)
    {
        _config = config;
        _updateFunc = updateFunc;
        _exitFlag = false;

        _ = Task.Run(() => Run(_cancellationTokenSource.Token));
    }

    private async Task Init(CancellationToken cancellationToken)
    {
        await Task.Delay(5000, cancellationToken);

        try
        {
            if (webSocket == null)
            {
                webSocket = new ClientWebSocket();
                await webSocket.ConnectAsync(new Uri(Url), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch { }
    }

    public void Close()
    {
        try
        {
            _exitFlag = true;
            _cancellationTokenSource.Cancel();
            if (webSocket != null)
            {
                webSocket.Abort();
                webSocket = null;
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
    }

    private async Task Run(CancellationToken cancellationToken)
    {
        try
        {
            await Init(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!_exitFlag && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1000, cancellationToken);
                if (!AppManager.Instance.IsRunningCore(ECoreType.sing_box))
                {
                    continue;
                }
                if (webSocket != null)
                {
                    if (webSocket.State is WebSocketState.Aborted or WebSocketState.Closed)
                    {
                        webSocket.Abort();
                        webSocket = null;
                        await Init(cancellationToken);
                        continue;
                    }

                    if (webSocket.State != WebSocketState.Open)
                    {
                        continue;
                    }

                    var buffer = new byte[1024];
                    var res = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                    while (!res.CloseStatus.HasValue && !cancellationToken.IsCancellationRequested)
                    {
                        var result = Encoding.UTF8.GetString(buffer, 0, res.Count);
                        if (result.IsNotEmpty())
                        {
                            ParseOutput(result, out var up, out var down);

                            await _updateFunc?.Invoke(new ServerSpeedItem()
                            {
                                ProxyUp = (long)(up / 1000),
                                ProxyDown = (long)(down / 1000)
                            });
                        }
                        res = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
            }
        }
    }

    private void ParseOutput(string source, out ulong up, out ulong down)
    {
        up = 0;
        down = 0;
        try
        {
            var trafficItem = JsonUtils.Deserialize<TrafficItem>(source);
            if (trafficItem != null)
            {
                up = trafficItem.Up;
                down = trafficItem.Down;
            }
        }
        catch
        {
        }
    }
}
