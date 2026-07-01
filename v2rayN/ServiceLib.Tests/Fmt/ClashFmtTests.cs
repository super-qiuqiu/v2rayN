using AwesomeAssertions;
using ServiceLib.Common;
using ServiceLib.Handler.Fmt;
using ServiceLib.Enums;
using ServiceLib.Models.Entities;
using Xunit;

namespace ServiceLib.Tests.Fmt;

public class ClashFmtTests
{
    // ── ResolveFullArray: basic Clash YAML ────────────────────────────

    [Fact]
    public void ResolveFullArray_ShouldReturnNull_WhenNotClashConfig()
    {
        var result = ClashFmt.ResolveFullArray("just some random text", null);
        result.Should().BeNull();
    }

    [Fact]
    public void ResolveFullArray_ShouldReturnNull_WhenNoProxies()
    {
        var yaml = """
                   port: 7890
                   rules:
                     - MATCH,direct
                   """;
        var result = ClashFmt.ResolveFullArray(yaml, null);
        result.Should().BeNull();
    }

    [Fact]
    public void ResolveFullArray_ShouldParseVMessProxy()
    {
        var yaml = """
                   port: 7890
                   rules:
                     - MATCH,direct
                   proxies:
                     - name: "test-vmess"
                       type: vmess
                       server: example.com
                       port: 443
                       uuid: "12345678-1234-1234-1234-123456789abc"
                       alterId: 0
                       cipher: auto
                       tls: true
                       servername: example.com
                       network: ws
                       ws-opts:
                         path: /path
                         headers:
                           Host: ws.example.com
                   """;
        var result = ClashFmt.ResolveFullArray(yaml, "sub");

        result.Should().NotBeNull();
        result!.Count.Should().Be(1);

        var item = result[0];
        item.ConfigType.Should().Be(EConfigType.VMess);
        item.Address.Should().Be("example.com");
        item.Port.Should().Be(443);
        item.Password.Should().Be("12345678-1234-1234-1234-123456789abc");
        item.StreamSecurity.Should().Be("tls");
        item.Sni.Should().Be("example.com");
        item.Remarks.Should().Contain("test-vmess");
    }

    [Fact]
    public void ResolveFullArray_ShouldParseVlessProxy()
    {
        var yaml = """
                   port: 7890
                   rules:
                     - MATCH,direct
                   proxies:
                     - name: "test-vless"
                       type: vless
                       server: vless.example.com
                       port: 443
                       uuid: "abcdef12-3456-7890-abcd-ef1234567890"
                       flow: xtls-rprx-vision
                       tls: true
                       servername: vless.example.com
                       client-fingerprint: chrome
                       network: tcp
                   """;
        var result = ClashFmt.ResolveFullArray(yaml, "mysub");

        result.Should().NotBeNull();
        result!.Count.Should().Be(1);

        var item = result[0];
        item.ConfigType.Should().Be(EConfigType.VLESS);
        item.Address.Should().Be("vless.example.com");
        item.Port.Should().Be(443);
        item.Password.Should().Be("abcdef12-3456-7890-abcd-ef1234567890");
        item.StreamSecurity.Should().Be("tls");
        item.Fingerprint.Should().Be("chrome");
    }

    [Fact]
    public void ResolveFullArray_ShouldParseTrojanProxy()
    {
        var yaml = """
                   port: 7890
                   rules:
                     - MATCH,direct
                   proxies:
                     - name: "test-trojan"
                       type: trojan
                       server: trojan.example.com
                       port: 443
                       password: "my-trojan-password"
                       sni: trojan.example.com
                   """;
        var result = ClashFmt.ResolveFullArray(yaml, null);

        result.Should().NotBeNull();
        result!.Count.Should().Be(1);

        var item = result[0];
        item.ConfigType.Should().Be(EConfigType.Trojan);
        item.Address.Should().Be("trojan.example.com");
        item.Port.Should().Be(443);
        item.Password.Should().Be("my-trojan-password");
        item.Sni.Should().Be("trojan.example.com");
    }

    [Fact]
    public void ResolveFullArray_ShouldParseShadowsocksProxy()
    {
        var yaml = """
                   port: 7890
                   rules:
                     - MATCH,direct
                   proxies:
                     - name: "test-ss"
                       type: ss
                       server: ss.example.com
                       port: 8388
                       cipher: aes-256-gcm
                       password: "ss-password"
                   """;
        var result = ClashFmt.ResolveFullArray(yaml, null);

        result.Should().NotBeNull();
        result!.Count.Should().Be(1);

        var item = result[0];
        item.ConfigType.Should().Be(EConfigType.Shadowsocks);
        item.Address.Should().Be("ss.example.com");
        item.Port.Should().Be(8388);
    }

    [Fact]
    public void ResolveFullArray_ShouldParseMieruProxy()
    {
        var yaml = """
                   port: 7890
                   rules:
                     - MATCH,direct
                   proxies:
                     - name: "mieru-node"
                       type: mieru
                       server: mieru.example.com
                       port: 2999
                       transport: TCP
                       username: user
                       password: mySecretPass
                       multiplexing: MULTIPLEXING_LOW
                       traffic-pattern: ""
                   """;
        var result = ClashFmt.ResolveFullArray(yaml, "sub");

        result.Should().NotBeNull();
        result!.Count.Should().Be(1);

        var item = result[0];
        item.ConfigType.Should().Be(EConfigType.Mieru);
        item.Address.Should().Be("mieru.example.com");
        item.Port.Should().Be(2999);
        item.Username.Should().Be("user");
        item.Password.Should().Be("mySecretPass");
        item.Remarks.Should().Contain("mieru-node");
    }

    [Fact]
    public void ResolveFullArray_ShouldParseMixedProxies_SkipUnsupported()
    {
        var yaml = """
                   port: 7890
                   rules:
                     - MATCH,direct
                   proxies:
                     - name: "vmess-1"
                       type: vmess
                       server: vmess.example.com
                       port: 443
                       uuid: "12345678-1234-1234-1234-123456789abc"
                       cipher: auto
                     - name: "mieru-1"
                       type: mieru
                       server: mieru.example.com
                       port: 443
                       password: mieruPass
                       transport: TCP
                     - name: "trojan-1"
                       type: trojan
                       server: trojan.example.com
                       port: 443
                       password: "pass123"
                       sni: trojan.example.com
                   """;
        var result = ClashFmt.ResolveFullArray(yaml, "test");

        result.Should().NotBeNull();
        result!.Count.Should().Be(3); // vmess + mieru + trojan (all three supported now)
        result[0].ConfigType.Should().Be(EConfigType.VMess);
        result[1].ConfigType.Should().Be(EConfigType.Mieru);
        result[2].ConfigType.Should().Be(EConfigType.Trojan);
    }

    [Fact]
    public void ResolveFullArray_ShouldPrefixRemarksWithSubRemarks()
    {
        var yaml = """
                   port: 7890
                   rules:
                     - MATCH,direct
                   proxies:
                     - name: "my-node"
                       type: trojan
                       server: example.com
                       port: 443
                       password: "pass"
                       sni: example.com
                   """;
        var result = ClashFmt.ResolveFullArray(yaml, "MySub");

        result.Should().NotBeNull();
        result![0].Remarks.Should().StartWith("MySub-");
    }

    [Fact]
    public void ResolveFullArray_ShouldParseHysteria2Proxy()
    {
        var yaml = """
                   port: 7890
                   rules:
                     - MATCH,direct
                   proxies:
                     - name: "test-hy2"
                       type: hysteria2
                       server: hy2.example.com
                       port: 443
                       password: "hy2-password"
                       sni: hy2.example.com
                   """;
        var result = ClashFmt.ResolveFullArray(yaml, null);

        result.Should().NotBeNull();
        result!.Count.Should().Be(1);

        var item = result[0];
        item.ConfigType.Should().Be(EConfigType.Hysteria2);
        item.Address.Should().Be("hy2.example.com");
        item.Port.Should().Be(443);
    }

    [Fact]
    public void ResolveFullArray_ShouldParseVlessWithReality()
    {
        var yaml = """
                   port: 7890
                   rules:
                     - MATCH,direct
                   proxies:
                     - name: "vless-reality"
                       type: vless
                       server: reality.example.com
                       port: 443
                       uuid: "abcdef12-3456-7890-abcd-ef1234567890"
                       tls: true
                       servername: www.microsoft.com
                       client-fingerprint: chrome
                       network: tcp
                       reality-opts:
                         public-key: "test-public-key-base64"
                         short-id: "abcdef12"
                   """;
        var result = ClashFmt.ResolveFullArray(yaml, null);

        result.Should().NotBeNull();
        result!.Count.Should().Be(1);

        var item = result[0];
        item.ConfigType.Should().Be(EConfigType.VLESS);
        item.StreamSecurity.Should().Be("reality");
        item.PublicKey.Should().Be("test-public-key-base64");
        item.ShortId.Should().Be("abcdef12");
    }

    [Fact]
    public void ResolveFullArray_ShouldParseGrpcTransport()
    {
        var yaml = """
                   port: 7890
                   rules:
                     - MATCH,direct
                   proxies:
                     - name: "vmess-grpc"
                       type: vmess
                       server: grpc.example.com
                       port: 443
                       uuid: "12345678-1234-1234-1234-123456789abc"
                       cipher: auto
                       tls: true
                       servername: grpc.example.com
                       network: grpc
                       grpc-opts:
                         grpc-service-name: my-service
                   """;
        var result = ClashFmt.ResolveFullArray(yaml, null);

        result.Should().NotBeNull();
        result!.Count.Should().Be(1);

        var item = result[0];
        item.ConfigType.Should().Be(EConfigType.VMess);
        // grpc service name should be parsed into transport extra
        item.Network.Should().Be("grpc");
    }

    [Fact]
    public void ResolveFullArray_ShouldNotSetCoreType_ProfileUsesSystemDefault()
    {
        var yaml = """
                   port: 7890
                   rules:
                     - MATCH,direct
                   proxies:
                     - name: "test-vmess"
                       type: vmess
                       server: example.com
                       port: 443
                       uuid: "12345678-1234-1234-1234-123456789abc"
                       cipher: auto
                   """;
        var result = ClashFmt.ResolveFullArray(yaml, null);

        result.Should().NotBeNull();
        // CoreType should NOT be set (null) so the system uses user's configured default
        result![0].CoreType.Should().BeNull();
    }

    // ── ResolveFull (original behavior preserved) ─────────────────────

    [Fact]
    public void ResolveFull_ShouldReturnCustomProfile_WhenClashConfig()
    {
        var yaml = """
                   port: 7890
                   socks-port: 7891
                   rules:
                     - MATCH,direct
                   proxies:
                     - name: test
                       type: vmess
                       server: example.com
                       port: 443
                   """;
        var result = ClashFmt.ResolveFull(yaml, "test-sub");

        result.Should().NotBeNull();
        result!.CoreType.Should().Be(ECoreType.mihomo);
        result.Remarks.Should().Be("test-sub");
    }

    [Fact]
    public void ResolveFull_ShouldReturnNull_WhenNotClashConfig()
    {
        var result = ClashFmt.ResolveFull("random text", null);
        result.Should().BeNull();
    }

    // ── AnyTLS proxy support ─────────────────────────────────────────

    [Fact]
    public void ResolveFullArray_ShouldParseAnytlsProxy_WithTlsAndSni()
    {
        var yaml = """
                   proxies:
                     - name: anytls-node
                       type: anytls
                       server: anytls.example.com
                       port: 443
                       password: mySecretPass
                       sni: anytls.example.com
                       client-fingerprint: chrome
                       alpn:
                         - h2
                         - http/1.1
                   """;
        var result = ClashFmt.ResolveFullArray(yaml, null);

        result.Should().NotBeNull();
        result!.Count.Should().Be(1);
        var item = result[0];
        item.ConfigType.Should().Be(EConfigType.Anytls);
        item.Address.Should().Be("anytls.example.com");
        item.Port.Should().Be(443);
        item.Password.Should().Be("mySecretPass");
        item.Sni.Should().Be("anytls.example.com");
        item.Fingerprint.Should().Be("chrome");
        item.StreamSecurity.Should().Be(Global.StreamSecurity); // tls
        item.Alpn.Should().Be("h2,http/1.1");
        item.CoreType.Should().BeNull(); // let system decide
    }

    [Fact]
    public void ResolveFullArray_ShouldParseAnytlsProxy_WithInsecure()
    {
        var yaml = """
                   proxies:
                     - name: anytls-insecure
                       type: anytls
                       server: insecure.example.com
                       port: 8443
                       password: pass123
                       insecure: true
                   """;
        var result = ClashFmt.ResolveFullArray(yaml, null);

        result.Should().NotBeNull();
        result!.Count.Should().Be(1);
        var item = result[0];
        item.ConfigType.Should().Be(EConfigType.Anytls);
        item.Address.Should().Be("insecure.example.com");
        item.Port.Should().Be(8443);
        item.Password.Should().Be("pass123");
        item.AllowInsecure.Should().Be(Global.StringTrue);
    }
}
