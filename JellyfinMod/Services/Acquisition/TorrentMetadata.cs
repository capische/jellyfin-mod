using System.Security.Cryptography;
using System.Text;

namespace JellyfinMod.Services.Acquisition;

/// <summary>
/// Derives the normalized BitTorrent v1 infohash from a torrent file or magnet link (P4.A5). Only a
/// 40-character lower-case SHA-1 identity is accepted; v2-only and hybrid torrents are refused because their
/// client identity has not been verified yet.
/// </summary>
public static class TorrentMetadata
{
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>Returns the v1 infohash of a magnet link, or throws <see cref="TorznabException"/>.</summary>
    public static TorrentLocator FromMagnet(string magnet)
    {
        if (!magnet.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase) || magnet.Length > 8192)
            throw new TorznabException("unsupported_hash", "The magnet link is not valid.");
        string? v1 = null;
        var hasV2 = false;
        foreach (var part in magnet[8..].Split('&'))
        {
            var separator = part.IndexOf('=', StringComparison.Ordinal);
            if (separator < 0 || !part[..separator].StartsWith("xt", StringComparison.OrdinalIgnoreCase)) continue;
            var value = Uri.UnescapeDataString(part[(separator + 1)..]);
            if (value.StartsWith("urn:btih:", StringComparison.OrdinalIgnoreCase))
                v1 = NormalizeHash(value[9..]) ?? throw new TorznabException("unsupported_hash", "The magnet infohash is not valid.");
            else if (value.StartsWith("urn:btmh:", StringComparison.OrdinalIgnoreCase)) hasV2 = true;
        }

        if (hasV2) throw new TorznabException("unsupported_hash", "BitTorrent v2 and hybrid magnets are not supported yet.");
        return v1 is null
            ? throw new TorznabException("unsupported_hash", "The magnet link has no BitTorrent v1 infohash.")
            : new TorrentLocator(v1, null, magnet, null, null);
    }

    /// <summary>Normalizes a hex or base32 v1 infohash to lower-case hex, or returns null.</summary>
    public static string? NormalizeHash(string? value)
    {
        if (value is null) return null;
        value = value.Trim();
        if (value.Length == 40 && value.All(Uri.IsHexDigit)) return value.ToLowerInvariant();
        if (value.Length != 32) return null;
        var bytes = new byte[20];
        var buffer = 0;
        var bits = 0;
        var index = 0;
        foreach (var character in value.ToUpperInvariant())
        {
            var digit = Base32Alphabet.IndexOf(character, StringComparison.Ordinal);
            if (digit < 0) return null;
            buffer = (buffer << 5) | digit;
            bits += 5;
            if (bits >= 8)
            {
                bytes[index++] = (byte)(buffer >> (bits - 8));
                bits -= 8;
            }
        }

        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>Parses a v1 torrent and hashes its exact <c>info</c> dictionary bytes.</summary>
    public static TorrentLocator FromTorrent(byte[] bytes)
    {
        try
        {
            if (bytes.Length == 0 || bytes[0] != (byte)'d') throw new FormatException();
            var position = 1;
            (int Start, int End)? info = null;
            while (bytes[position] != (byte)'e')
            {
                var key = ReadString(bytes, ref position);
                var start = position;
                Skip(bytes, ref position, 0);
                if (Encoding.ASCII.GetString(key) == "info") info = (start, position);
            }

            if (info is not { } span || bytes[span.Start] != (byte)'d') throw new FormatException();
            var dictionary = ReadDictionary(bytes, span.Start);
            var hasV1 = dictionary.ContainsKey("pieces");
            var hasV2 = dictionary.TryGetValue("meta version", out var metaVersion) && metaVersion is long version && version >= 2;
            if (!hasV1) throw new TorznabException("unsupported_hash", "BitTorrent v2-only torrents are not supported yet.");
            if (hasV2) throw new TorznabException("unsupported_hash", "Hybrid BitTorrent v1/v2 torrents are not supported yet.");
            long? size = dictionary.GetValueOrDefault("length") as long?;
            if (size is null && dictionary.GetValueOrDefault("files") is List<object?> files)
                size = files.OfType<Dictionary<string, object?>>().Sum(file => file.GetValueOrDefault("length") as long? ?? 0);
            var name = dictionary.GetValueOrDefault("name") is byte[] nameBytes ? Encoding.UTF8.GetString(nameBytes) : null;
            var hash = Convert.ToHexStringLower(SHA1.HashData(bytes.AsSpan(span.Start, span.End - span.Start)));
            return new TorrentLocator(hash, bytes, null, size, name);
        }
        catch (Exception error) when (error is FormatException or IndexOutOfRangeException or ArgumentOutOfRangeException or OverflowException)
        {
            throw new TorznabException("invalid_torrent", "The indexer returned an invalid torrent file.");
        }
    }

    private static Dictionary<string, object?> ReadDictionary(byte[] bytes, int position)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        position++;
        while (bytes[position] != (byte)'e')
        {
            var key = Encoding.UTF8.GetString(ReadString(bytes, ref position));
            result[key] = ReadValue(bytes, ref position, 1);
        }

        return result;
    }

    private static object? ReadValue(byte[] bytes, ref int position, int depth)
    {
        if (depth > 32) throw new FormatException();
        switch (bytes[position])
        {
            case (byte)'i':
                var end = Array.IndexOf(bytes, (byte)'e', position);
                if (end < 0) throw new FormatException();
                var number = long.Parse(Encoding.ASCII.GetString(bytes, position + 1, end - position - 1),
                    System.Globalization.CultureInfo.InvariantCulture);
                position = end + 1;
                return number;
            case (byte)'l':
                position++;
                var list = new List<object?>();
                while (bytes[position] != (byte)'e') list.Add(ReadValue(bytes, ref position, depth + 1));
                position++;
                return list;
            case (byte)'d':
                position++;
                var dictionary = new Dictionary<string, object?>(StringComparer.Ordinal);
                while (bytes[position] != (byte)'e')
                {
                    var key = Encoding.UTF8.GetString(ReadString(bytes, ref position));
                    dictionary[key] = ReadValue(bytes, ref position, depth + 1);
                }

                position++;
                return dictionary;
            default:
                return ReadString(bytes, ref position);
        }
    }

    private static void Skip(byte[] bytes, ref int position, int depth) => ReadValue(bytes, ref position, depth);

    private static byte[] ReadString(byte[] bytes, ref int position)
    {
        var colon = Array.IndexOf(bytes, (byte)':', position);
        if (colon < 0 || colon - position > 10) throw new FormatException();
        var length = int.Parse(Encoding.ASCII.GetString(bytes, position, colon - position), System.Globalization.CultureInfo.InvariantCulture);
        if (length < 0 || colon + 1 + length > bytes.Length) throw new FormatException();
        position = colon + 1 + length;
        return bytes.AsSpan(colon + 1, length).ToArray();
    }
}
