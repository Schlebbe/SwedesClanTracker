using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace SwedesClanTracker.RankTransition;

public static class TransitionEnvironmentIdentityReader
{
    public static async Task<TransitionEnvironmentIdentity> ReadAsync(
        DbContext db,
        IConfiguration configuration,
        CancellationToken ct = default)
    {
        var databaseIdentity = db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true
            ? await ReadPostgresIdentityAsync(db, ct)
            : db.Database.ProviderName ?? "unknown-provider";
        var womGroupId = configuration.GetValue<int>("WiseOldMan:GroupId");
        var guildId = configuration.GetValue<ulong>("DiscordBot:GuildId");
        return new TransitionEnvironmentIdentity(
            databaseIdentity,
            womGroupId,
            guildId == 0 ? "" : guildId.ToString());
    }

    private static async Task<string> ReadPostgresIdentityAsync(DbContext db, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT current_database() || '@' || COALESCE(inet_server_addr()::text, 'local') || ':' || inet_server_port()";
        var value = await command.ExecuteScalarAsync(ct);
        return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "unknown-postgres";
    }
}
