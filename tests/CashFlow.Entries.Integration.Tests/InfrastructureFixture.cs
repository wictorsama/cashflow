using CashFlow.Entries.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace CashFlow.Entries.Integration.Tests;

/// <summary>Real PostgreSQL and RabbitMQ in Docker, started once and shared by all tests.</summary>
public sealed class InfrastructureFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:4-management-alpine").Build();

    public string RabbitMqUri => _rabbit.GetConnectionString();

    public async Task InitializeAsync() =>
        await Task.WhenAll(_postgres.StartAsync(), _rabbit.StartAsync());

    public async Task DisposeAsync()
    {
        await _postgres.DisposeAsync();
        await _rabbit.DisposeAsync();
    }

    /// <summary>Creates an empty, fully migrated database so each test starts from a clean state.</summary>
    public async Task<string> CreateDatabaseAsync()
    {
        var name = "entries_" + Guid.NewGuid().ToString("N");

        await using (var admin = new NpgsqlConnection(_postgres.GetConnectionString()))
        {
            await admin.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE {name}", admin);
            await command.ExecuteNonQueryAsync();
        }

        var connectionString = new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Database = name }
            .ConnectionString;

        await using var db = NewContext(connectionString);
        await db.Database.MigrateAsync();

        return connectionString;
    }

    public static EntriesDbContext NewContext(string connectionString) =>
        new(new DbContextOptionsBuilder<EntriesDbContext>().UseNpgsql(connectionString).Options);
}
