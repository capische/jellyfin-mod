using System.Text;
using Jellyfin.Extensions;
using MediaBrowser.Controller.Configuration;

namespace JellyfinMod.Services;

/// <summary>Builds catalog title keys with the running Jellyfin server's sorting rules.</summary>
public sealed class CatalogSortName(IServerConfigurationManager configurationManager)
{
    /// <summary>Returns the normalized key used to interleave an entry with native library items.</summary>
    public string GetKey(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        var configuration = configurationManager.Configuration;
        var sortable = title.Trim().ToLowerInvariant();

        foreach (var word in configuration.SortRemoveWords)
        {
            if (sortable.StartsWith(word + " ", StringComparison.Ordinal))
            {
                sortable = sortable[(word.Length + 1)..];
            }

            sortable = sortable.Replace(" " + word + " ", " ", StringComparison.Ordinal);
            if (sortable.EndsWith(" " + word, StringComparison.Ordinal))
            {
                sortable = sortable[..^(word.Length + 1)];
            }
        }

        foreach (var character in configuration.SortRemoveCharacters)
        {
            sortable = sortable.Replace(character, string.Empty, StringComparison.Ordinal);
        }

        foreach (var character in configuration.SortReplaceCharacters)
        {
            sortable = sortable.Replace(character, " ", StringComparison.Ordinal);
        }

        return NormalizeChunks(sortable);
    }

    /// <summary>Returns a case-insensitive, diacritic-folded key for title search.</summary>
    public string GetSearchKey(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        var normalized = title.Trim().ToLowerInvariant().RemoveDiacritics();
        return normalized.All(char.IsAscii) ? normalized : normalized.Transliterated();
    }

    private static string NormalizeChunks(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        var chunkStart = 0;
        var digitChunk = char.IsDigit(value[0]);
        for (var index = 1; index < value.Length; index++)
        {
            var digit = char.IsDigit(value[index]);
            if (digit == digitChunk)
            {
                continue;
            }

            AppendChunk(builder, digitChunk, value[chunkStart..index]);
            chunkStart = index;
            digitChunk = digit;
        }

        AppendChunk(builder, digitChunk, value[chunkStart..]);
        var normalized = builder.ToString().RemoveDiacritics();
        return normalized.All(char.IsAscii) ? normalized : normalized.Transliterated();
    }

    private static void AppendChunk(StringBuilder builder, bool digitChunk, ReadOnlySpan<char> chunk)
    {
        if (digitChunk && chunk.Length < 10)
        {
            builder.Append('0', 10 - chunk.Length);
        }

        builder.Append(chunk);
    }
}
