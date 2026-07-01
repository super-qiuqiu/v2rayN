namespace ServiceLib.Services.CoreConfig;

public partial class CoreConfigSingboxService(CoreConfigContext context)
{
    private static readonly string _tag = "CoreConfigSingboxService";
    private readonly Config _config = context.AppConfig;
    private readonly ProfileItem _node = context.Node;

    private SingboxConfig _coreConfig = new();

    #region public gen function

    public RetResult GenerateClientConfigContent()
    {
        var ret = new RetResult();
        try
        {
            if (_node == null
                || !_node.IsValid())
            {
                ret.Msg = ResUI.CheckServerSettings;
                return ret;
            }
            if (_node.GetNetwork() is nameof(ETransport.kcp) or nameof(ETransport.xhttp))
            {
                ret.Msg = ResUI.Incorrectconfiguration + $" - {_node.GetNetwork()}";
                return ret;
            }

            ret.Msg = ResUI.InitialConfiguration;

            var result = EmbedUtils.GetEmbedText(Global.SingboxSampleClient);
            if (result.IsNullOrEmpty())
            {
                ret.Msg = ResUI.FailedGetDefaultConfiguration;
                return ret;
            }

            _coreConfig = JsonUtils.Deserialize<SingboxConfig>(result);
            if (_coreConfig == null)
            {
                ret.Msg = ResUI.FailedGenDefaultConfiguration;
                return ret;
            }

            GenLog();

            GenInbounds();

            GenOutbounds();

            GenRouting();

            GenDns();

            GenExperimental();

            ConvertGeo2Ruleset();

            ApplyOutboundBindInterface();
            ApplyOutboundSendThrough();

            ret.Msg = string.Format(ResUI.SuccessfulConfiguration, "");
            ret.Success = true;

            ret.Data = ApplyFullConfigTemplate();
            return ret;
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            ret.Msg = ResUI.FailedGenDefaultConfiguration;
            return ret;
        }
    }

    public RetResult GenerateClientSpeedtestConfig(List<ServerTestItem> selecteds)
    {
        var ret = new RetResult();
        try
        {
            ret.Msg = ResUI.InitialConfiguration;

            var result = EmbedUtils.GetEmbedText(Global.SingboxSampleClient);
            var txtOutbound = EmbedUtils.GetEmbedText(Global.SingboxSampleOutbound);
            if (result.IsNullOrEmpty() || txtOutbound.IsNullOrEmpty())
            {
                ret.Msg = ResUI.FailedGetDefaultConfiguration;
                return ret;
            }

            _coreConfig = JsonUtils.Deserialize<SingboxConfig>(result);
            if (_coreConfig == null)
            {
                ret.Msg = ResUI.FailedGenDefaultConfiguration;
                return ret;
            }

            var (lstIpEndPoints, lstTcpConns) = Utils.GetActiveNetworkInfo();

            GenLog();
            GenMinimizedDns();
            _coreConfig.inbounds.Clear();
            _coreConfig.outbounds.RemoveAt(0);

            var initPort = AppManager.Instance.GetLocalPort(EInboundProtocol.speedtest);

            foreach (var it in selecteds)
            {
                if (!(Global.SingboxSupportConfigType.Contains(it.ConfigType) || it.ConfigType.IsGroupType()))
                {
                    continue;
                }
                if (!it.ConfigType.IsComplexType() && it.Port <= 0)
                {
                    continue;
                }
                var actIndexId = context.ServerTestItemMap.GetValueOrDefault(it.IndexId, it.IndexId);
                var item = context.AllProxiesMap.GetValueOrDefault(actIndexId);
                if (item is null || item.ConfigType is EConfigType.Custom || !item.IsValid())
                {
                    continue;
                }

                //find unused port
                var port = initPort;
                for (var k = initPort; k < Global.MaxPort; k++)
                {
                    if (lstIpEndPoints?.FindIndex(_it => _it.Port == k) >= 0)
                    {
                        continue;
                    }
                    if (lstTcpConns?.FindIndex(_it => _it.LocalEndPoint.Port == k) >= 0)
                    {
                        continue;
                    }
                    //found
                    port = k;
                    initPort = port + 1;
                    break;
                }

                //Port In Used
                if (lstIpEndPoints?.FindIndex(_it => _it.Port == port) >= 0)
                {
                    continue;
                }
                it.Port = port;
                it.AllowTest = true;

                //inbound
                Inbound4Sbox inbound = new()
                {
                    listen = Global.Loopback,
                    listen_port = port,
                    type = nameof(EInboundProtocol.mixed),
                };
                inbound.tag = inbound.type + inbound.listen_port.ToString();
                _coreConfig.inbounds.Add(inbound);

                var tag = Global.ProxyTag + inbound.listen_port.ToString();
                var serverList = new CoreConfigSingboxService(context with { Node = item }).BuildAllProxyOutbounds(tag);
                FillRangeProxy(serverList, _coreConfig, false);

                //rule
                Rule4Sbox rule = new()
                {
                    inbound = new List<string> { inbound.tag },
                    outbound = tag
                };
                _coreConfig.route.rules.Add(rule);
            }

            ApplyOutboundBindInterface();
            ApplyOutboundSendThrough();

            var json = JsonUtils.Serialize(_coreConfig);

            // Post-process: inject mieru-specific fields that conflict with inherited types
            // sing-box mieru outbound uses "transport" as a string ("tcp"/"udp"), not a Transport object
            if (_node.ConfigType == EConfigType.Mieru)
            {
                json = PostProcessMieruOutbound(json);
            }

            ret.Success = true;
            ret.Data = json;
            return ret;
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            ret.Msg = ResUI.FailedGenDefaultConfiguration;
            return ret;
        }
    }

    public RetResult GenerateClientSpeedtestConfig(int port)
    {
        var ret = new RetResult();
        try
        {
            if (_node == null
                || !_node.IsValid())
            {
                ret.Msg = ResUI.CheckServerSettings;
                return ret;
            }
            if (_node.GetNetwork() is nameof(ETransport.kcp) or nameof(ETransport.xhttp))
            {
                ret.Msg = ResUI.Incorrectconfiguration + $" - {_node.GetNetwork()}";
                return ret;
            }

            ret.Msg = ResUI.InitialConfiguration;

            var result = EmbedUtils.GetEmbedText(Global.SingboxSampleClient);
            if (result.IsNullOrEmpty())
            {
                ret.Msg = ResUI.FailedGetDefaultConfiguration;
                return ret;
            }

            _coreConfig = JsonUtils.Deserialize<SingboxConfig>(result);
            if (_coreConfig == null)
            {
                ret.Msg = ResUI.FailedGenDefaultConfiguration;
                return ret;
            }

            GenLog();
            GenOutbounds();
            GenMinimizedDns();

            _coreConfig.route.rules.Clear();
            _coreConfig.inbounds.Clear();
            _coreConfig.inbounds.Add(new()
            {
                tag = $"{EInboundProtocol.mixed}{port}",
                listen = Global.Loopback,
                listen_port = port,
                type = nameof(EInboundProtocol.mixed),
            });
            ApplyOutboundBindInterface();
            ApplyOutboundSendThrough();

            ret.Msg = string.Format(ResUI.SuccessfulConfiguration, "");
            ret.Success = true;
            ret.Data = JsonUtils.Serialize(_coreConfig);
            return ret;
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            ret.Msg = ResUI.FailedGenDefaultConfiguration;
            return ret;
        }
    }

    #endregion public gen function

    #region mieru post-process

    /// <summary>
    /// Post-process JSON to inject mieru-specific "transport" string field.
    /// In sing-box mieru outbound, "transport" is a string ("tcp"/"udp"),
    /// not the Transport4Sbox object used by other outbound types.
    /// We strip any existing transport object and inject the string value.
    /// </summary>
    private string PostProcessMieruOutbound(string json)
    {
        try
        {
            var protocolExtra = _node.GetProtocolExtra();
            var transportStr = protocolExtra.MieruTransport?.ToLowerInvariant() ?? "tcp";
            var multiplexing = protocolExtra.MieruMultiplexing ?? "MULTIPLEXING_LOW";

            // Find the proxy outbound and inject/replace transport string
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            var outbounds = root.GetProperty("outbounds");
            var outArr = new List<Dictionary<string, object?>>();
            foreach (var ob in outbounds.EnumerateArray())
            {
                var dict = new Dictionary<string, object?>();
                foreach (var prop in ob.EnumerateObject())
                {
                    if (prop.Name == "transport")
                    {
                        // Replace with mieru string value
                        dict["transport"] = transportStr;
                        continue;
                    }
                    dict[prop.Name] = prop.Value.ValueKind == System.Text.Json.JsonValueKind.String
                        ? prop.Value.GetString()
                        : prop.Value.ValueKind == System.Text.Json.JsonValueKind.Number
                            ? prop.Value.TryGetInt64(out var lng) ? (object?)lng : prop.Value.GetDouble()
                            : prop.Value.ValueKind == System.Text.Json.JsonValueKind.True || prop.Value.ValueKind == System.Text.Json.JsonValueKind.False
                                ? prop.Value.GetBoolean()
                                : (object?)System.Text.Json.JsonSerializer.Deserialize<object?>(prop.Value.GetRawText());
                }
                // Ensure multiplexing and traffic_pattern are set for proxy outbound
                if (dict.TryGetValue("type", out var typeVal) && typeVal?.ToString() == "mieru")
                {
                    dict["multiplexing"] = multiplexing;
                    if (protocolExtra.MieruTrafficPattern.IsNotEmpty())
                    {
                        dict["traffic_pattern"] = protocolExtra.MieruTrafficPattern;
                    }
                }
                outArr.Add(dict);
            }

            // Re-serialize with outbounds replaced
            var resultDict = new Dictionary<string, object?>();
            foreach (var prop in root.EnumerateObject())
            {
                if (prop.Name == "outbounds")
                {
                    resultDict["outbounds"] = outArr;
                }
                else
                {
                    resultDict[prop.Name] = System.Text.Json.JsonSerializer.Deserialize<object?>(prop.Value.GetRawText());
                }
            }

            return System.Text.Json.JsonSerializer.Serialize(resultDict, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            });
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            return json; // fallback: return original JSON
        }
    }

    #endregion mieru post-process
}
