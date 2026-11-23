using CashFlow.Consolidation.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Testcontainers.Redis;

namespace CashFlow.Consolidation.Integration.Tests;

/// <summary>Real PostgreSQL, RabbitMQ and Redis in Docker, started once and shared by all tests.</summary>
public sealed class InfrastructureFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:4-management-alpine").Build();
    private readonly RedisContainer _redis = new RedisBuilder("redis:7-alpine").Build();

    public string RedisConnectionString => _redis.GetConnectionString();

    public async Task InitializeAsync() =>
        await Task.WhenAll(_postgres.StartAsync(), _rabbit.StartAsync(), _redis.StartAsync());

    public async Task DisposeAsync()
    {
        await _postgres.DisposeAsync();
        await _rabbit.DisposeAsync();
        await _redis.DisposeAsync();
    }

    /// <summary>An empty database with the Consolidation migrations applied.</summary>
    public async Task<string> CreateConsolidationDatabaseAsync()
    {
        var connection = await CreateEmptyDatabaseAsync();
        await using var db = NewContext(connection);
        await db.Database.MigrateAsync();

        return connection;
    }

    /// <summary>An empty database with the Entries migrations applied (the producer side of the end-to-end tests).</summary>
    public async Task<string> CreateEntriesDatabaseAsync()
    {
        var connection = await CreateEmptyDatabaseAsync();
        var options = new DbContextOptionsBuilder<Entries.Infrastructure.Persistence.EntriesDbContext>().UseNpgsql(connection).Options;
        await using var db = new Entries.Infrastructure.Persistence.EntriesDbContext(options);
        await db.Database.MigrateAsync();

        return connection;
    }

    /// <summary>A private virtual host, so tests with a running consumer never see each other's messages.</summary>
    public async Task<string> CreateVirtualHostAsync()
    {
        var name = "vh_" + Guid.NewGuid().ToString("N");
        await _rabbit.ExecAsync(["rabbitmqctl", "add_vhost", name]);
        await _rabbit.ExecAsync(["rabbitmqctl", "set_permissions", "-p", name, "rabbitmq", ".*", ".*", ".*"]);

        return new UriBuilder(_rabbit.GetConnectionString()) { Path = "/" + name }.Uri.ToString();
    }

    public static ConsolidationDbContext NewContext(string connectionString) =>
        new(new DbContextOptionsBuilder<ConsolidationDbContext>().UseNpgsql(connectionString).Options);

    private async Task<string> CreateEmptyDatabaseAsync()
    {
        var name = "db_" + Guid.NewGuid().ToString("N");

        await using var admin = new NpgsqlConnection(_postgres.GetConnectionString());
        await admin.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE {name}", admin);
        await command.ExecuteNonQueryAsync();

        return new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Database = name }.ConnectionString;
    }
}
