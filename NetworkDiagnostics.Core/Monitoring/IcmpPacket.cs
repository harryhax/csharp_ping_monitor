using System.Buffers.Binary;
using NetworkDiagnostics.Core.Models;

namespace NetworkDiagnostics.Core.Monitoring;

/// <summary>Result of matching a received datagram against an outstanding echo request.</summary>
public enum IcmpMatch
{
    /// <summary>Not related to our request (another process's traffic, or a stale reply).</summary>
    Unrelated,
    EchoReply,
    DestinationUnreachable,
    TimeExceeded,
}

/// <summary>Minimal ICMPv4 echo construction and parsing, used by socket-based probes.</summary>
public static class IcmpPacket
{
    public const byte EchoRequest = 8;
    public const byte EchoReply = 0;
    public const byte DestUnreachable = 3;
    public const byte TimeExceeded = 11;
    public const int HeaderLength = 8;

    public static byte[] BuildEchoRequest(ushort identifier, ushort sequence, int payloadLength = 32)
    {
        var packet = new byte[HeaderLength + payloadLength];
        packet[0] = EchoRequest;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4), identifier);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(6), sequence);
        for (var i = 0; i < payloadLength; i++)
        {
            packet[HeaderLength + i] = (byte)('a' + (i % 23));
        }

        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), Checksum(packet));
        return packet;
    }

    /// <summary>RFC 1071 internet checksum.</summary>
    public static ushort Checksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        var i = 0;
        for (; i + 1 < data.Length; i += 2)
        {
            sum += (uint)((data[i] << 8) | data[i + 1]);
        }

        if (i < data.Length)
        {
            sum += (uint)(data[i] << 8);
        }

        while ((sum >> 16) != 0)
        {
            sum = (sum & 0xFFFF) + (sum >> 16);
        }

        return (ushort)~sum;
    }

    /// <summary>
    /// Inspects a received datagram. Handles buffers that start with an IPv4 header (macOS datagram ICMP
    /// sockets) as well as buffers that start directly with the ICMP header.
    /// </summary>
    public static IcmpMatch Match(ReadOnlySpan<byte> datagram, ushort identifier, ushort sequence, out byte code)
    {
        code = 0;
        var icmp = StripIPv4Header(datagram);
        if (icmp.Length < HeaderLength)
        {
            return IcmpMatch.Unrelated;
        }

        var type = icmp[0];
        code = icmp[1];

        if (type == EchoReply)
        {
            return ReadIdSeq(icmp) == (identifier, sequence) ? IcmpMatch.EchoReply : IcmpMatch.Unrelated;
        }

        if (type is DestUnreachable or TimeExceeded)
        {
            // Payload holds the original IP header + first 8 bytes of our echo request.
            var original = StripIPv4Header(icmp[HeaderLength..], requireHeader: true);
            if (original.Length >= HeaderLength && original[0] == EchoRequest && ReadIdSeq(original) == (identifier, sequence))
            {
                return type == DestUnreachable ? IcmpMatch.DestinationUnreachable : IcmpMatch.TimeExceeded;
            }
        }

        return IcmpMatch.Unrelated;
    }

    public static string DescribeUnreachableCode(byte code) => code switch
    {
        0 => "Destination network unreachable",
        1 => "Destination host unreachable",
        2 => "Destination protocol unreachable",
        3 => "Destination port unreachable",
        4 => "Fragmentation needed",
        9 or 10 or 13 => "Communication administratively prohibited",
        _ => $"Destination unreachable (code {code})",
    };

    public static ProbeStatus ToStatus(IcmpMatch match) => match switch
    {
        IcmpMatch.EchoReply => ProbeStatus.Success,
        IcmpMatch.DestinationUnreachable => ProbeStatus.DestinationUnreachable,
        IcmpMatch.TimeExceeded => ProbeStatus.TtlExpired,
        _ => ProbeStatus.Error,
    };

    private static (ushort, ushort) ReadIdSeq(ReadOnlySpan<byte> icmp) =>
        (BinaryPrimitives.ReadUInt16BigEndian(icmp[4..]), BinaryPrimitives.ReadUInt16BigEndian(icmp[6..]));

    private static ReadOnlySpan<byte> StripIPv4Header(ReadOnlySpan<byte> data, bool requireHeader = false)
    {
        if (data.Length >= 20 && (data[0] >> 4) == 4)
        {
            var ihl = (data[0] & 0x0F) * 4;
            if (ihl >= 20 && data.Length >= ihl)
            {
                return data[ihl..];
            }
        }

        return requireHeader ? ReadOnlySpan<byte>.Empty : data;
    }
}
