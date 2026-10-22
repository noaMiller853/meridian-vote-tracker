using Npgsql;

namespace Meridian.Infrastructure;

/// <summary>Turns a <c>postgres://user:password@host:port/database</c> URL (what Render and Heroku-style platforms provide) into an Npgsql connection string. Any other value is returned unchanged.</summary>
public static class ConnectionStringNormalizer
{
    public static string Normalize(string connectionString)
    {
        var value = connectionString.Trim();
        if (!value.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) &&
            !value.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
            return connectionString;

        var uri = new Uri(value);
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 5432,
            Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')),
            SslMode = SslMode.Prefer,
        };
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var parts = uri.UserInfo.Split(':', 2);
            builder.Username = Uri.UnescapeDataString(parts[0]);
            if (parts.Length > 1) builder.Password = Uri.UnescapeDataString(parts[1]);
        }
        return builder.ConnectionString;
    }
}
