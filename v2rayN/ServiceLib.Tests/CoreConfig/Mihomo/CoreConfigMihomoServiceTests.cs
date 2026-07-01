using AwesomeAssertions;
using ServiceLib.Enums;
using ServiceLib.Manager;
using ServiceLib.Models.Dto;
using ServiceLib.Services.CoreConfig;
using Xunit;

namespace ServiceLib.Tests.CoreConfig.Mihomo;

public class CoreConfigMihomoServiceTests
{
    [Fact]
    public void GenerateClientConfigContent_Mieru_ShouldGenerateMihomoYaml()
    {
        var config = CoreConfigTestFactory.CreateConfig(ECoreType.mihomo);
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var node = CreateMieruNode();
        var context = CoreConfigTestFactory.CreateContext(config, node, ECoreType.mihomo);

        var result = new CoreConfigMihomoService(context).GenerateClientConfigContent();

        result.Success.Should().BeTrue($"ret msg: {result.Msg}");
        var yaml = result.Data!.ToString();
        yaml.Should().Contain("mixed-port:");
        yaml.Should().Contain("type: mieru");
        yaml.Should().Contain("transport: TCP");
        yaml.Should().Contain("multiplexing: MULTIPLEXING_LOW");
        yaml.Should().Contain("MATCH,mieru-demo");
    }

    [Fact]
    public void GetCoreType_CustomDummy_ShouldKeepExplicitMihomoCoreType()
    {
        var config = CoreConfigTestFactory.CreateConfig(ECoreType.sing_box);
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var dummyNode = new ProfileItem
        {
            ConfigType = EConfigType.Custom,
            CoreType = ECoreType.mihomo,
        };

        var coreType = AppManager.Instance.GetCoreType(dummyNode, dummyNode.ConfigType);

        coreType.Should().Be(ECoreType.mihomo);
    }

    [Fact]
    public void GenerateClientSpeedtestConfig_Mieru_ShouldCreateListenerBoundToProxy()
    {
        var config = CoreConfigTestFactory.CreateConfig(ECoreType.mihomo);
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var node = CreateMieruNode();
        var context = CoreConfigTestFactory.CreateContext(config, node, ECoreType.mihomo);
        var testItem = new ServerTestItem
        {
            IndexId = node.IndexId,
            Profile = node,
            ConfigType = node.ConfigType,
            CoreType = ECoreType.mihomo,
            Address = node.Address,
            Port = node.Port,
        };

        var result = new CoreConfigMihomoService(context).GenerateClientSpeedtestConfig([testItem]);

        result.Success.Should().BeTrue($"ret msg: {result.Msg}");
        testItem.AllowTest.Should().BeTrue();
        testItem.Port.Should().BeGreaterThan(0);
        testItem.TestApiPort.Should().BeGreaterThan(0);
        testItem.TestProxyName.Should().Be($"proxy{testItem.Port}");
        var yaml = result.Data!.ToString();
        yaml.Should().Contain("external-controller: 127.0.0.1:");
        yaml.Should().Contain("listeners:");
        yaml.Should().Contain($"IN-NAME,mixed{testItem.Port},proxy{testItem.Port}");
        yaml.Should().Contain("type: mieru");
    }

    private static ProfileItem CreateMieruNode()
    {
        var node = new ProfileItem
        {
            IndexId = "mieru-1",
            ConfigType = EConfigType.Mieru,
            CoreType = ECoreType.mihomo,
            Remarks = "mieru-demo",
            Address = "example.com",
            Port = 2999,
            Username = "user",
            Password = "pass",
            Network = nameof(ETransport.raw),
            StreamSecurity = string.Empty,
            Subid = string.Empty,
        };
        node.SetProtocolExtra(node.GetProtocolExtra() with
        {
            MieruTransport = "TCP",
            MieruMultiplexing = "MULTIPLEXING_LOW",
        });
        return node;
    }
}
