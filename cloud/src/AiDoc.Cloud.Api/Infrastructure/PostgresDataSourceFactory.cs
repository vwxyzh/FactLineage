using Azure.Core;
using AiDoc.Cloud.Api.Configuration;
using Microsoft.Extensions.Options;
using Npgsql;

namespace AiDoc.Cloud.Api.Infrastructure;

public static class PostgresDataSourceFactory
{
    private static readonly TokenRequestContext TokenRequest = new(["https://ossrdbms-aad.database.windows.net/.default"]);

    public static NpgsqlDataSource Create(IServiceProvider services)
    {
        var credential = services.GetRequiredService<TokenCredential>();
        var options = services.GetRequiredService<IOptions<CloudOptions>>().Value.PostgreSql;
        var connectionString = new NpgsqlConnectionStringBuilder
        {
            Host = options.Host,
            Port = options.Port,
            Database = options.Database,
            Username = options.User,
            SslMode = SslMode.Require,
            Timeout = 15,
            CommandTimeout = 30
        };
        var builder = new NpgsqlDataSourceBuilder(connectionString.ConnectionString);
        builder.UsePeriodicPasswordProvider(
            async (_, cancellationToken) => (await credential.GetTokenAsync(TokenRequest, cancellationToken)).Token,
            TimeSpan.FromMinutes(50),
            TimeSpan.FromSeconds(10));
        return builder.Build();
    }
}