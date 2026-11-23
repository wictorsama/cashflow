using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CashFlow.Entries.Infrastructure.Persistence;

/// <summary>Used only by "dotnet ef" to generate migrations; it never connects to a real database.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<EntriesDbContext>
{
    public EntriesDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<EntriesDbContext>()
            .UseNpgsql("Host=localhost;Database=entries_design")
            .Options);
}
