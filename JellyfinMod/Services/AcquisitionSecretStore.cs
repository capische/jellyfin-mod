using System.Security.Cryptography;
using System.Text.Json;

namespace JellyfinMod.Services;

/// <summary>
/// Holds indexer and download-client credentials outside SQLite and the XML plugin configuration (user decision 3).
/// </summary>
/// <remarks>
/// Rows keep only an opaque reference. Values live in one plugin-owned file created with mode 0600 in the plugin
/// data directory and replaced atomically. Nothing here logs, formats or returns a value except to the code that
/// sends it to its own configured endpoint.
/// </remarks>
public sealed class AcquisitionSecretStore
{
    private const string Prefix = "sec_";
    private readonly string _path;
    // One gate per store file, so the plugin's own instance and the DI singleton never interleave writes.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate;

    /// <summary>Initializes a new instance of the <see cref="AcquisitionSecretStore"/> class.</summary>
    /// <param name="dataPath">The plugin data directory.</param>
    public AcquisitionSecretStore(string dataPath)
    {
        _path = Path.GetFullPath(Path.Combine(dataPath, "acquisition-secrets.json"));
        _gate = Gates.GetOrAdd(_path, static _ => new SemaphoreSlim(1, 1));
    }

    /// <summary>Stores a new value and returns its opaque reference.</summary>
    public async Task<string> AddAsync(string value, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        var reference = Prefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        await MutateAsync(values => values[reference] = value, cancellationToken).ConfigureAwait(false);
        return reference;
    }

    /// <summary>Removes a value; an unknown or null reference is ignored.</summary>
    public Task RemoveAsync(string? reference, CancellationToken cancellationToken) => reference is null
        ? Task.CompletedTask
        : MutateAsync(values => values.Remove(reference), cancellationToken);

    /// <summary>Returns the value, or null when the reference is absent or the store cannot be read.</summary>
    public async Task<string?> GetAsync(string? reference, CancellationToken cancellationToken)
    {
        if (reference is null) return null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return Read().GetValueOrDefault(reference);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Returns true when the reference resolves to a stored value.</summary>
    public async Task<bool> HasAsync(string? reference, CancellationToken cancellationToken) =>
        await GetAsync(reference, cancellationToken).ConfigureAwait(false) is not null;

    private async Task MutateAsync(Action<Dictionary<string, string>> change, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A damaged store is never overwritten, so one bad write cannot erase every other credential.
            var values = ReadStrict();
            change(values);
            var temporary = _path + ".tmp";
            var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temporary, options))
            {
                await JsonSerializer.SerializeAsync(stream, values, cancellationToken: cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, _path, true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private Dictionary<string, string> ReadStrict()
    {
        if (!File.Exists(_path)) return new(StringComparer.Ordinal);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path)) is { } values
            ? new Dictionary<string, string>(values, StringComparer.Ordinal)
            : throw new InvalidDataException("The acquisition secret store is unreadable.");
    }

    private Dictionary<string, string> Read()
    {
        try
        {
            return ReadStrict();
        }
        catch (InvalidDataException)
        {
            return new(StringComparer.Ordinal);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            // Never guess or partially trust a damaged store; affected configurations report the secret as missing.
            return new(StringComparer.Ordinal);
        }
    }
}
