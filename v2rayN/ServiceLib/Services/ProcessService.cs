namespace ServiceLib.Services;

public class ProcessService : IDisposable
{
    private const int OutputBatchSize = 100;
    private static readonly TimeSpan OutputBatchInterval = TimeSpan.FromMilliseconds(500);
    private readonly Process _process;
    private readonly Func<bool, string, Task>? _updateFunc;
    private readonly ConcurrentQueue<string> _pendingOutput = new();
    private readonly SemaphoreSlim _outputSignal = new(0);
    private readonly CancellationTokenSource _outputCancellationTokenSource = new();
    private Task? _outputPumpTask;
    private int _outputSignalState;
    private bool _isDisposed;

    public int Id => _process.Id;
    public IntPtr Handle => _process.Handle;
    public bool HasExited => _process.HasExited;
    public string ProcessName => _process.ProcessName;

    public ProcessService(
        string fileName,
        string arguments,
        string workingDirectory,
        bool displayLog,
        bool redirectInput,
        Dictionary<string, string>? environmentVars,
        Func<bool, string, Task>? updateFunc)
    {
        _updateFunc = updateFunc;

        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardInput = redirectInput,
                RedirectStandardOutput = displayLog,
                RedirectStandardError = displayLog,
                CreateNoWindow = true,
                StandardOutputEncoding = displayLog ? Encoding.UTF8 : null,
                StandardErrorEncoding = displayLog ? Encoding.UTF8 : null,
            },
            EnableRaisingEvents = true
        };

        if (environmentVars != null)
        {
            foreach (var kv in environmentVars)
            {
                _process.StartInfo.Environment[kv.Key] = kv.Value;
            }
        }

        if (displayLog)
        {
            _outputPumpTask = Task.Run(ProcessOutputQueueAsync);
            RegisterEventHandlers();
        }
    }

    public async Task StartAsync(string pwd = null)
    {
        _process.Start();

        if (_process.StartInfo.RedirectStandardOutput)
        {
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }

        if (_process.StartInfo.RedirectStandardInput)
        {
            await Task.Delay(10);
            await _process.StandardInput.WriteLineAsync(pwd);
        }
    }

    public async Task StopAsync()
    {
        if (_process.HasExited)
        {
            await StopOutputPumpAsync();
            return;
        }

        try
        {
            if (_process.StartInfo.RedirectStandardOutput)
            {
                try
                {
                    _process.CancelOutputRead();
                }
                catch { }
                try
                {
                    _process.CancelErrorRead();
                }
                catch { }
            }
            await StopOutputPumpAsync();

            try
            {
                if (Utils.IsNonWindows())
                {
                    _process.Kill(true);
                }
            }
            catch { }

            try
            {
                _process.Kill();
            }
            catch { }

            await Task.Delay(100);
        }
        catch (Exception ex)
        {
            await _updateFunc?.Invoke(true, ex.Message);
        }
    }

    public void Detach()
    {
        if (_isDisposed)
        {
            return;
        }

        try
        {
            if (_process.StartInfo.RedirectStandardOutput)
            {
                try
                {
                    _process.CancelOutputRead();
                }
                catch { }
                try
                {
                    _process.CancelErrorRead();
                }
                catch { }
            }

            _process.EnableRaisingEvents = false;
            StopOutputPump();
            _process.Dispose();
        }
        catch (Exception ex)
        {
            _updateFunc?.Invoke(true, ex.Message);
        }

        _isDisposed = true;
        GC.SuppressFinalize(this);
    }

    private void RegisterEventHandlers()
    {
        void dataHandler(object sender, DataReceivedEventArgs e)
        {
            if (e.Data.IsNotEmpty())
            {
                _pendingOutput.Enqueue(e.Data + Environment.NewLine);
                SignalOutput();
            }
        }

        _process.OutputDataReceived += dataHandler;
        _process.ErrorDataReceived += dataHandler;

        _process.Exited += (s, e) =>
        {
            try
            {
                _process.OutputDataReceived -= dataHandler;
                _process.ErrorDataReceived -= dataHandler;
                SignalOutput();
            }
            catch
            {
            }
        };
    }

    private async Task ProcessOutputQueueAsync()
    {
        var token = _outputCancellationTokenSource.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await _outputSignal.WaitAsync(token);
                if (_pendingOutput.Count < OutputBatchSize)
                {
                    await Task.Delay(OutputBatchInterval, token);
                }

                Interlocked.Exchange(ref _outputSignalState, 0);
                await FlushOutputAsync();
                SignalOutputIfPending();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Logging.SaveLog(nameof(ProcessService), ex);
            }
        }

        try
        {
            await FlushOutputAsync();
        }
        catch (Exception ex)
        {
            Logging.SaveLog(nameof(ProcessService), ex);
        }
    }

    private async Task FlushOutputAsync()
    {
        if (_updateFunc == null)
        {
            while (_pendingOutput.TryDequeue(out _))
            {
            }
            return;
        }

        var sb = new StringBuilder();
        var lineCount = 0;
        while (_pendingOutput.TryDequeue(out var line))
        {
            sb.Append(line);
            lineCount++;
            if (lineCount < OutputBatchSize)
            {
                continue;
            }

            await _updateFunc(false, sb.ToString());
            sb.Clear();
            lineCount = 0;
        }

        if (sb.Length > 0)
        {
            await _updateFunc(false, sb.ToString());
        }
    }

    private void SignalOutput()
    {
        if (Interlocked.Exchange(ref _outputSignalState, 1) == 0)
        {
            _outputSignal.Release();
        }
    }

    private void SignalOutputIfPending()
    {
        if (!_pendingOutput.IsEmpty)
        {
            SignalOutput();
        }
    }

    private async Task StopOutputPumpAsync()
    {
        if (_outputPumpTask == null)
        {
            return;
        }

        _outputCancellationTokenSource.Cancel();
        SignalOutput();
        await _outputPumpTask;
        _outputPumpTask = null;
    }

    private void StopOutputPump()
    {
        if (_outputPumpTask == null)
        {
            return;
        }

        _outputCancellationTokenSource.Cancel();
        SignalOutput();
        try
        {
            _outputPumpTask.Wait(OutputBatchInterval);
        }
        catch
        {
        }
        _outputPumpTask = null;
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        try
        {
            if (!_process.HasExited)
            {
                try
                {
                    _process.CancelOutputRead();
                }
                catch { }
                try
                {
                    _process.CancelErrorRead();
                }
                catch { }

                _process.Kill();
            }

            StopOutputPump();
            _process.Dispose();
        }
        catch (Exception ex)
        {
            _updateFunc?.Invoke(true, ex.Message);
        }

        _isDisposed = true;
        GC.SuppressFinalize(this);
    }
}
