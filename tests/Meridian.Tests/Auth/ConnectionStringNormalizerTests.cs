using Meridian.Infrastructure;
using Npgsql;

namespace Meridian.Tests.Auth;

public sealed class ConnectionStringNormalizerTests
{
    [Fact]
    public void A_postgres_url_becomes_an_npgsql_connection_string()
    {
        var result = new NpgsqlConnectionStringBuilder(
            ConnectionStringNormalizer.Normalize("postgres://meridian:p%40ss%3Aword@dpg-abc123:5432/meridian_db"));

        Assert.Equal("dpg-abc123", result.Host);
        Assert.Equal(5432, result.Port);
        Assert.Equal("meridian_db", result.Database);
        Assert.Equal("meridian", result.Username);
        Assert.Equal("p@ss:word", result.Password);
    }

    [Fact]
    public void A_url_without_a_port_uses_5432()
    {
        var result = new NpgsqlConnectionStringBuilder(ConnectionStringNormalizer.Normalize("postgresql://u:p@db.example.com/mydb"));

        Assert.Equal(5432, result.Port);
        Assert.Equal("mydb", result.Database);
    }

    [Fact]
    public void A_key_value_connection_string_is_returned_unchanged()
    {
        const string value = "Host=localhost;Port=5432;Database=meridian;Username=meridian;Password=x";

        Assert.Equal(value, ConnectionStringNormalizer.Normalize(value));
    }
}
