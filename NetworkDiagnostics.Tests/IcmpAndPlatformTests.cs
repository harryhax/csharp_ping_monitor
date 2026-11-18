using NetworkDiagnostics.App.Controls;
using NetworkDiagnostics.App.Platforms.MacOS;
using NetworkDiagnostics.Core.Monitoring;

namespace NetworkDiagnostics.Tests;

public class IcmpPacketTests
{
    [Fact]
    public void EchoRequest_HasValidChecksum()
    {
        var p = IcmpPacket.BuildEchoRequest(0x1234, 7);
        Assert.Equal(8, p[0]);
        Assert.Equal(0, IcmpPacket.Checksum(p)); // a correct checksum makes the total sum 0xFFFF → ~ = 0
    }

    [Fact]
    public void EchoReply_Matches_WithAndWithoutIpHeader()
    {
        var reply = IcmpPacket.BuildEchoRequest(0x1234, 7);
        reply[0] = IcmpPacket.EchoReply;
        Assert.Equal(IcmpMatch.EchoReply, IcmpPacket.Match(reply, 0x1234, 7, out _));
        Assert.Equal(IcmpMatch.EchoReply, IcmpPacket.Match(WithIpHeader(reply), 0x1234, 7, out _));
    }

    [Fact]
    public void EchoReply_ForOtherIdOrSequence_IsUnrelated()
    {
        var reply = IcmpPacket.BuildEchoRequest(0x1234, 7);
        reply[0] = IcmpPacket.EchoReply;
        Assert.Equal(IcmpMatch.Unrelated, IcmpPacket.Match(reply, 0x1234, 8, out _));
        Assert.Equal(IcmpMatch.Unrelated, IcmpPacket.Match(reply, 0x9999, 7, out _));
    }

    [Fact]
    public void DestinationUnreachable_ForOurRequest_IsMatched()
    {
        var original = WithIpHeader(IcmpPacket.BuildEchoRequest(0x1234, 7))[..28];
        var error = new byte[8 + original.Length];
        error[0] = IcmpPacket.DestUnreachable;
        error[1] = 1;
        original.CopyTo(error, 8);

        Assert.Equal(IcmpMatch.DestinationUnreachable, IcmpPacket.Match(WithIpHeader(error), 0x1234, 7, out var code));
        Assert.Equal(1, code);
        Assert.Equal(IcmpMatch.Unrelated, IcmpPacket.Match(WithIpHeader(error), 0x1234, 8, out _));
    }

    [Fact]
    public void Truncated_IsUnrelated() => Assert.Equal(IcmpMatch.Unrelated, IcmpPacket.Match(new byte[3], 1, 1, out _));

    private static byte[] WithIpHeader(byte[] icmp)
    {
        var buf = new byte[20 + icmp.Length];
        buf[0] = 0x45;
        buf[9] = 1;
        icmp.CopyTo(buf, 20);
        return buf;
    }
}

public class PlatformParsingTests
{
    [Fact]
    public void NetworkSetupOutput_IsParsed()
    {
        const string output = """

            Hardware Port: Ethernet Adapter (en4)
            Device: en4
            Ethernet Address: 00:00:00:00:00:00

            Hardware Port: Wi-Fi
            Device: en0
            Ethernet Address: 00:00:00:00:00:01

            VLAN Configurations
            ===================
            """;
        var map = MacHardwarePortMap.Parse(output);
        Assert.Equal("Wi-Fi", map["en0"]);
        Assert.Equal("Ethernet Adapter (en4)", map["en4"]);
        Assert.Equal(2, map.Count);
    }

    [Theory]
    [InlineData(0.3, 0.5)]
    [InlineData(1.1, 2)]
    [InlineData(3, 5)]
    [InlineData(7, 10)]
    [InlineData(12, 20)]
    [InlineData(180, 200)]
    [InlineData(0, 1)]
    public void GraphAxis_NiceCeiling(double input, double expected) =>
        Assert.Equal(expected, LatencyGraph.NiceCeiling(input), 9);
}
