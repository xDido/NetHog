using System.Buffers.Binary;
using System.Text;

namespace NetHog.Services;

internal static class DomainTrafficInspector
{
    public static string? TryReadDomain(byte[] frame, int ipOffset)
    {
        if (ipOffset < 0 || frame.Length <= ipOffset) return null;
        var version = frame[ipOffset] >> 4;
        if (version is not (4 or 6)) return null;
        var headerLength = version == 4 ? (frame[ipOffset] & 0x0F) * 4 : 40;
        if (headerLength < 20 || frame.Length < ipOffset + headerLength) return null;
        var protocol = version == 4 ? frame[ipOffset + 9] : frame[ipOffset + 6];
        var transportOffset = ipOffset + headerLength;
        if (protocol == 6) return TryReadTcpDomain(frame, transportOffset);
        if (protocol == 17) return TryReadDnsDomain(frame, transportOffset);
        return null;
    }

    private static string? TryReadTcpDomain(byte[] frame, int transportOffset)
    {
        if (frame.Length < transportOffset + 20) return null;
        var dataOffset = (frame[transportOffset + 12] >> 4) * 4;
        if (dataOffset < 20 || frame.Length < transportOffset + dataOffset) return null;
        var payloadOffset = transportOffset + dataOffset;
        if (payloadOffset >= frame.Length) return null;
        var payload = frame.AsSpan(payloadOffset);
        return TryReadTlsServerName(payload) ?? TryReadHttpHost(payload);
    }

    private static string? TryReadDnsDomain(byte[] frame, int transportOffset)
    {
        if (frame.Length < transportOffset + 8) return null;
        var sourcePort = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(transportOffset, 2));
        var destinationPort = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(transportOffset + 2, 2));
        if (sourcePort != 53 && destinationPort != 53) return null;
        var payloadOffset = transportOffset + 8;
        if (frame.Length < payloadOffset + 13) return null;
        var payload = frame.AsSpan(payloadOffset);
        var questionCount = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(4, 2));
        if (questionCount == 0) return null;
        var index = 12;
        var labels = new List<string>();
        while (index < payload.Length)
        {
            var length = payload[index++];
            if (length == 0) break;
            if ((length & 0xC0) != 0 || length > 63 || index + length > payload.Length) return null;
            labels.Add(Encoding.ASCII.GetString(payload.Slice(index, length)));
            index += length;
        }

        return labels.Count == 0
            ? null
            : DomainObservationStore.NormalizeDomain(string.Join('.', labels));
    }

    private static string? TryReadTlsServerName(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 5 || payload[0] != 0x16 || payload[1] != 0x03) return null;
        var recordLength = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(3, 2));
        if (recordLength < 4 || payload.Length < 5 + recordLength || payload[5] != 0x01) return null;
        var hello = payload.Slice(5);
        if (hello.Length < 4 + 2 + 32 + 1) return null;
        var handshakeLength = (hello[1] << 16) | (hello[2] << 8) | hello[3];
        if (handshakeLength + 4 > hello.Length) return null;
        var index = 4 + 2 + 32;
        var sessionIdLength = hello[index++];
        index += sessionIdLength;
        if (index + 2 > hello.Length) return null;
        var cipherLength = BinaryPrimitives.ReadUInt16BigEndian(hello.Slice(index, 2));
        index += 2 + cipherLength;
        if (index >= hello.Length) return null;
        index += 1 + hello[index];
        if (index + 2 > hello.Length) return null;
        var extensionsLength = BinaryPrimitives.ReadUInt16BigEndian(hello.Slice(index, 2));
        index += 2;
        var end = Math.Min(hello.Length, index + extensionsLength);
        while (index + 4 <= end)
        {
            var extensionType = BinaryPrimitives.ReadUInt16BigEndian(hello.Slice(index, 2));
            var extensionLength = BinaryPrimitives.ReadUInt16BigEndian(hello.Slice(index + 2, 2));
            index += 4;
            if (index + extensionLength > end) return null;
            if (extensionType == 0 && extensionLength >= 5)
            {
                var extension = hello.Slice(index, extensionLength);
                var listLength = BinaryPrimitives.ReadUInt16BigEndian(extension);
                var cursor = 2;
                while (cursor + 3 <= extension.Length && cursor < listLength + 2)
                {
                    var nameType = extension[cursor++];
                    var nameLength = BinaryPrimitives.ReadUInt16BigEndian(extension.Slice(cursor, 2));
                    cursor += 2;
                    if (nameType == 0 && cursor + nameLength <= extension.Length)
                    {
                        return DomainObservationStore.NormalizeDomain(
                            Encoding.ASCII.GetString(extension.Slice(cursor, nameLength)));
                    }
                    cursor += nameLength;
                }
            }
            index += extensionLength;
        }
        return null;
    }

    private static string? TryReadHttpHost(ReadOnlySpan<byte> payload)
    {
        var length = Math.Min(payload.Length, 4096);
        var text = Encoding.ASCII.GetString(payload[..length]);
        if (!text.StartsWith("GET ", StringComparison.OrdinalIgnoreCase)
            && !text.StartsWith("POST ", StringComparison.OrdinalIgnoreCase)
            && !text.StartsWith("HEAD ", StringComparison.OrdinalIgnoreCase)
            && !text.StartsWith("PUT ", StringComparison.OrdinalIgnoreCase)
            && !text.StartsWith("CONNECT ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        foreach (var line in text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase)) continue;
            return DomainObservationStore.NormalizeDomain(line[5..].Trim().Split(':')[0]);
        }
        return null;
    }
}
