using Microsoft.EntityFrameworkCore.Design;

namespace JellyfinMod.Data;

/// <summary>Creates the context for migration tooling without a running Jellyfin host.</summary>
public sealed class ModDbContextFactory : IDesignTimeDbContextFactory<ModDbContext>
{
    /// <inheritdoc />
    public ModDbContext CreateDbContext(string[] args)
        => new(Path.Combine(Path.GetTempPath(), "jellyfinmod-design.db"));
}
