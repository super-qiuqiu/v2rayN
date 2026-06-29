namespace ServiceLib.Manager;

/// <summary>
/// Core process processing class
/// </summary>
public class CoreManager
{
    private static readonly Lazy<CoreManager> _instance = new(() => new());
    public static CoreManager Instance => _instance.Value;
    private Config _config;
    [SupportedOSPlatform("windows")]
    private WindowsJobService? _processJob;
    private ProcessService? _processService;
    private ProcessService? _processPreService;
    private CoreInfo? _processCoreInfo;
    private string? _processConfigPath;
    private CoreInfo? _processPreCoreInfo;
    private string? _processPreConfigPath;
    private bool _linuxSudo = false;
    private Func<bool, string, Task>? _updateFunc;
    private const string _tag = "CoreHandler";

    public bool HasRunningCore => _processService is { HasExited: false } || _processPreService is { HasExited: false };
    public bool HasDetachedCore => File.Exists(GetDetachedCoreStatePath());

    public async Task Init(Config config, Func<bool, string, Task> updateFunc)
    {
        _config = config;
        _updateFunc = updateFunc;

        //Copy the bin folder to the storage location (for init)
        if (Environment.GetEnvironmentVariable(Global.LocalAppData) == "1")
        {
            var fromPath = Utils.GetBaseDirectory("bin");
            var toPath = Utils.GetBinPath("");
            if (fromPath != toPath)
            {
                FileUtils.CopyDirectory(fromPath, toPath, true, false);
            }
        }

        if (Utils.IsNonWindows())
        {
            var coreInfo = CoreInfoManager.Instance.GetCoreInfo();
            foreach (var it in coreInfo)
            {
                if (it.CoreType == ECoreType.v2rayN)
                {
                    if (Utils.UpgradeAppExists(out var upgradeFileName))
                    {
                        await Utils.SetLinuxChmod(upgradeFileName);
                    }
                    continue;
                }

                foreach (var name in it.CoreExes)
                {
                    var exe = Utils.GetBinPath(Utils.GetExeName(name), it.CoreType.ToString());
                    if (File.Exists(exe))
                    {
                        await Utils.SetLinuxChmod(exe);
                    }
                }
            }
        }
    }

    /// <param name="mainContext">Resolved main context (with pre-socks ports already merged if applicable).</param>
    /// <param name="preContext">Optional pre-socks context passed to <see cref="CoreStartPreService"/>.</param>
    public async Task LoadCore(CoreConfigContext? mainContext, CoreConfigContext? preContext)
    {
        if (mainContext == null)
        {
            await UpdateFunc(false, ResUI.CheckServerSettings);
            return;
        }

        var node = mainContext.Node;
        var fileName = Utils.GetBinConfigPath(Global.CoreConfigFileName);
        var result = await CoreConfigHandler.GenerateClientConfig(mainContext, fileName);
        if (result.Success != true)
        {
            await UpdateFunc(true, result.Msg);
            return;
        }

        await UpdateFunc(false, $"{node.GetSummary()}");
        await UpdateFunc(false, $"{Utils.GetRuntimeInfo()}");
        await UpdateFunc(false, string.Format(ResUI.StartService, DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss")));
        await CoreStop();
        await Task.Delay(100);

        if (Utils.IsWindows() && _config.TunModeItem.EnableTun)
        {
            await Task.Delay(100);
            await WindowsUtils.RemoveTunDevice();
        }

        await CoreStart(mainContext);
        await WaitForProxyPort(preContext);
        await CoreStartPreService(preContext);

        AppManager.Instance.RunningCoreType = preContext?.RunCoreType ?? mainContext.RunCoreType;

        if (_processService != null)
        {
            await UpdateFunc(true, $"{node.GetSummary()}");
        }
    }

    public async Task<ProcessService?> LoadCoreConfigSpeedtest(List<ServerTestItem> selecteds)
    {
        var coreType = selecteds.FirstOrDefault()?.CoreType == ECoreType.sing_box ? ECoreType.sing_box : ECoreType.Xray;
        var fileName = string.Format(Global.CoreSpeedtestConfigFileName, Utils.GetGuid(false));
        var configPath = Utils.GetBinConfigPath(fileName);
        var result = await CoreConfigHandler.GenerateClientSpeedtestConfig(_config, configPath, selecteds, coreType);
        await UpdateFunc(false, result.Msg);
        if (result.Success != true)
        {
            return null;
        }

        await UpdateFunc(false, string.Format(ResUI.StartService, DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss")));
        await UpdateFunc(false, configPath);

        var coreInfo = CoreInfoManager.Instance.GetCoreInfo(coreType);
        return await RunProcess(coreInfo, fileName, true, false);
    }

    public async Task<ProcessService?> LoadCoreConfigSpeedtest(ServerTestItem testItem)
    {
        var node = await AppManager.Instance.GetProfileItem(testItem.IndexId);
        if (node is null)
        {
            return null;
        }

        var fileName = string.Format(Global.CoreSpeedtestConfigFileName, Utils.GetGuid(false));
        var configPath = Utils.GetBinConfigPath(fileName);
        var (context, _) = await CoreConfigContextBuilder.Build(_config, node);
        var result = await CoreConfigHandler.GenerateClientSpeedtestConfig(_config, context, testItem, configPath);
        if (result.Success != true)
        {
            return null;
        }

        var coreType = context.RunCoreType;
        var coreInfo = CoreInfoManager.Instance.GetCoreInfo(coreType);
        return await RunProcess(coreInfo, fileName, true, false);
    }

    public async Task CoreStop()
    {
        try
        {
            await StopDetachedCores();

            if (_linuxSudo)
            {
                await CoreAdminManager.Instance.KillProcessAsLinuxSudo();
                _linuxSudo = false;
            }

            if (_processService != null)
            {
                await _processService.StopAsync();
                _processService.Dispose();
                _processService = null;
            }

            if (_processPreService != null)
            {
                await _processPreService.StopAsync();
                _processPreService.Dispose();
                _processPreService = null;
            }

            ClearProcessMetadata();
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
    }

    public async Task<bool> DetachCoreForAppExit()
    {
        try
        {
            var hasMainProcess = _processService is { HasExited: false } && _processCoreInfo != null && _processConfigPath.IsNotEmpty();
            var hasPreProcess = _processPreService is { HasExited: false } && _processPreCoreInfo != null && _processPreConfigPath.IsNotEmpty();

            if (!hasMainProcess && !hasPreProcess)
            {
                return true;
            }

            var mainCoreInfo = _processCoreInfo;
            var mainConfigPath = _processConfigPath;
            var preCoreInfo = _processPreCoreInfo;
            var preConfigPath = _processPreConfigPath;

            if (_linuxSudo)
            {
                await CoreAdminManager.Instance.KillProcessAsLinuxSudo();
                _linuxSudo = false;
                _processService?.Dispose();
            }
            else
            {
                await StopProcessOnly(_processService);
            }

            await StopProcessOnly(_processPreService);
            _processService = null;
            _processPreService = null;

            ProcessService? mainProcess = null;
            ProcessService? preProcess = null;
            List<DetachedCoreProcess> detachedProcesses = [];

            if (hasMainProcess)
            {
                mainProcess = await RunProcess(mainCoreInfo, mainConfigPath!, false, true, false);
                if (mainProcess is null)
                {
                    return false;
                }
                detachedProcesses.Add(CreateDetachedCoreProcess(mainProcess));
            }

            if (hasPreProcess)
            {
                preProcess = await RunProcess(preCoreInfo, preConfigPath!, false, true, false);
                if (preProcess is null)
                {
                    await StopProcessOnly(mainProcess);
                    return false;
                }
                detachedProcesses.Add(CreateDetachedCoreProcess(preProcess));
            }

            SaveDetachedCoreState(detachedProcesses);
            mainProcess?.Detach();
            preProcess?.Detach();
            _linuxSudo = false;
            ClearProcessMetadata();
            Logging.SaveLog("Core process detached for GUI exit");
            return true;
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            return false;
        }
    }

    #region Private

    private async Task CoreStart(CoreConfigContext context)
    {
        var node = context.Node;
        var coreType = AppManager.Instance.GetCoreType(node, node.ConfigType);
        var coreInfo = CoreInfoManager.Instance.GetCoreInfo(coreType);

        var displayLog = node.ConfigType != EConfigType.Custom || node.DisplayLog;
        var proc = await RunProcess(coreInfo, Global.CoreConfigFileName, displayLog, true);
        if (proc is null)
        {
            return;
        }
        _processService = proc;
        _processCoreInfo = coreInfo;
        _processConfigPath = Global.CoreConfigFileName;
    }

    private async Task CoreStartPreService(CoreConfigContext? preContext)
    {
        if (_processService is { HasExited: false } && preContext != null)
        {
            var preCoreType = preContext?.Node?.CoreType ?? ECoreType.sing_box;
            var fileName = Utils.GetBinConfigPath(Global.CorePreConfigFileName);
            var result = await CoreConfigHandler.GenerateClientConfig(preContext, fileName);
            if (result.Success)
            {
                var coreInfo = CoreInfoManager.Instance.GetCoreInfo(preCoreType);
                var proc = await RunProcess(coreInfo, Global.CorePreConfigFileName, true, true);
                if (proc is null)
                {
                    return;
                }
                _processPreService = proc;
                _processPreCoreInfo = coreInfo;
                _processPreConfigPath = Global.CorePreConfigFileName;
            }
        }
    }

    private static async Task StopProcessOnly(ProcessService? processService)
    {
        if (processService is null)
        {
            return;
        }

        await processService.StopAsync();
        processService.Dispose();
    }

    private async Task StopDetachedCores()
    {
        var statePath = GetDetachedCoreStatePath();
        if (!File.Exists(statePath))
        {
            return;
        }

        try
        {
            var state = JsonUtils.Deserialize<DetachedCoreState>(await File.ReadAllTextAsync(statePath));
            foreach (var processState in state?.Processes ?? [])
            {
                KillDetachedProcess(processState);
            }

            foreach (var pid in state?.Pids?.Distinct() ?? [])
            {
                KillDetachedProcess(new DetachedCoreProcess { Pid = pid });
            }

            await Task.Delay(100);
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
        finally
        {
            try
            {
                File.Delete(statePath);
            }
            catch { }
        }
    }

    private static void KillDetachedProcess(DetachedCoreProcess processState)
    {
        var pid = processState.Pid;
        if (pid <= 0 || pid == Environment.ProcessId)
        {
            return;
        }

        try
        {
            var process = Process.GetProcessById(pid);
            if (processState.ProcessName.IsNotEmpty()
                && !process.ProcessName.Equals(processState.ProcessName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!process.HasExited)
            {
                process.Kill(true);
            }
        }
        catch { }
    }

    private static void SaveDetachedCoreState(List<DetachedCoreProcess> processes)
    {
        var statePath = GetDetachedCoreStatePath();
        var state = new DetachedCoreState
        {
            Processes = processes
                .Where(process => process.Pid > 0)
                .GroupBy(process => process.Pid)
                .Select(group => group.First())
                .ToList()
        };

        if (state.Processes.Count == 0)
        {
            try
            {
                File.Delete(statePath);
            }
            catch { }
            return;
        }

        File.WriteAllText(statePath, JsonUtils.Serialize(state, false));
    }

    private static DetachedCoreProcess CreateDetachedCoreProcess(ProcessService processService)
    {
        return new DetachedCoreProcess
        {
            Pid = processService.Id,
            ProcessName = processService.ProcessName
        };
    }

    private static string GetDetachedCoreStatePath()
    {
        return Utils.GetBinConfigPath(Global.DetachedCoreStateFileName);
    }

    private void ClearProcessMetadata()
    {
        _processCoreInfo = null;
        _processConfigPath = null;
        _processPreCoreInfo = null;
        _processPreConfigPath = null;
    }

    private async Task UpdateFunc(bool notify, string msg)
    {
        await _updateFunc?.Invoke(notify, msg);
    }

    private static async Task WaitForProxyPort(CoreConfigContext? preContext, int timeoutMs = 5000)
    {
        if (preContext is null)
        {
            return;
        }
        if (!preContext.AppConfig.TunModeItem.EnableTun)
        {
            return;
        }

        using var rootCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs));
        var rootToken = rootCts.Token;

        var port = preContext.Node.Port;
        // SOCKS5 client greeting: VER=5, NMETHODS=1, METHOD=0x00 (no auth)
        ReadOnlyMemory<byte> greeting = new byte[] { 0x05, 0x01, 0x00 };
        var buf = new byte[2];

        while (!rootToken.IsCancellationRequested)
        {
            using var tcp = new TcpClient();
            using var attemptCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(rootToken, attemptCts.Token);
            var linkedToken = linkedCts.Token;
            try
            {
                await tcp.ConnectAsync(Global.Loopback, port, linkedToken);
                var stream = tcp.GetStream();

                await stream.WriteAsync(greeting, linkedToken);

                var read = await stream.ReadAsync(buf.AsMemory(0, 2), linkedToken);

                // Server selection: VER=5, METHOD=0x00 — proxy is fully ready
                if (read == 2 && buf[0] == 0x05)
                {
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                if (!rootToken.IsCancellationRequested)
                {
                    continue;
                }
                Logging.SaveLog($"WaitForProxyPort Timeout waiting for proxy port {port} to be ready.");
                return;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
            {
                // Connection refused, proxy not ready yet, wait 50ms before retrying
                try
                {
                    await Task.Delay(50, rootToken);
                }
                catch (OperationCanceledException)
                {
                    Logging.SaveLog($"WaitForProxyPort Timeout waiting for proxy port {port} to be ready.");
                    return;
                }
            }
            catch
            {
                // Ignore other exceptions and continue
            }
        }
    }

    #endregion Private

    #region Process

    private async Task<ProcessService?> RunProcess(CoreInfo? coreInfo, string configPath, bool displayLog, bool mayNeedSudo, bool attachToAppJob = true)
    {
        var fileName = CoreInfoManager.Instance.GetCoreExecFile(coreInfo, out var msg);
        if (fileName.IsNullOrEmpty())
        {
            await UpdateFunc(false, msg);
            return null;
        }

        try
        {
            if (mayNeedSudo
                && _config.TunModeItem.EnableTun
                && (coreInfo.CoreType is ECoreType.sing_box or ECoreType.mihomo or ECoreType.Xray)
                && Utils.IsNonWindows())
            {
                _linuxSudo = true;
                await CoreAdminManager.Instance.Init(_config, _updateFunc);
                return await CoreAdminManager.Instance.RunProcessAsLinuxSudo(fileName, coreInfo, configPath, displayLog);
            }

            return await RunProcessNormal(fileName, coreInfo, configPath, displayLog, attachToAppJob);
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            await UpdateFunc(mayNeedSudo, ex.Message);
            return null;
        }
    }

    private async Task<ProcessService?> RunProcessNormal(string fileName, CoreInfo? coreInfo, string configPath, bool displayLog, bool attachToAppJob)
    {
        var environmentVars = new Dictionary<string, string>();
        foreach (var kv in coreInfo.Environment)
        {
            environmentVars[kv.Key] = string.Format(kv.Value, coreInfo.AbsolutePath ? Utils.GetBinConfigPath(configPath).AppendQuotes() : configPath);
        }

        var procService = new ProcessService(
            fileName: fileName,
            arguments: string.Format(coreInfo.Arguments, coreInfo.AbsolutePath ? Utils.GetBinConfigPath(configPath).AppendQuotes() : configPath),
            workingDirectory: Utils.GetBinConfigPath(),
            displayLog: displayLog,
            redirectInput: false,
            environmentVars: environmentVars,
            updateFunc: _updateFunc
        );

        await procService.StartAsync();

        await Task.Delay(100);

        if (procService is null or { HasExited: true })
        {
            throw new Exception(ResUI.FailedToRunCore);
        }

        if (attachToAppJob)
        {
            AddProcessJob(procService.Handle);
        }

        return procService;
    }

    private void AddProcessJob(nint processHandle)
    {
        if (Utils.IsWindows())
        {
            _processJob ??= new();
            try
            {
                _processJob?.AddProcess(processHandle);
            }
            catch { }
        }
    }

    #endregion Process

    private sealed class DetachedCoreState
    {
        public List<DetachedCoreProcess> Processes { get; set; } = [];
        public List<int> Pids { get; set; } = [];
    }

    private sealed class DetachedCoreProcess
    {
        public int Pid { get; set; }
        public string? ProcessName { get; set; }
    }
}
