using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SealedFga.Fga.Outbox;

namespace SealedFga.Tests.Support;

/// <summary>
///     A DbContext over the test entities plus the SealedFGA outbox table. Backed by SQLite so we get
///     real change-tracking, keys and transactions (closer to a real consumer than the InMemory
///     provider). Value conversions for the strongly-typed IDs are registered exactly as the generated
///     <c>ConfigureSealedFga()</c> extension would, but written against EF Core 9 directly here.
/// </summary>
public class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options) {
    public DbSet<TestObjectEntity> Objects => Set<TestObjectEntity>();
    public DbSet<TestUserEntity> Users => Set<TestUserEntity>();
    public DbSet<TestParentEntity> Parents => Set<TestParentEntity>();
    public DbSet<TestJoinEntity> Joins => Set<TestJoinEntity>();
    public DbSet<TestGrantEntity> Grants => Set<TestGrantEntity>();
    public DbSet<SealedFgaOutboxEntry> Outbox => Set<SealedFgaOutboxEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) {
        base.OnModelCreating(modelBuilder);
        // Register the outbox entity the same way SealedFgaModelCustomizer does for a real consumer.
        modelBuilder.ConfigureSealedFgaOutbox();
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) {
        base.ConfigureConventions(configurationBuilder);
        configurationBuilder.Properties<TestObjectId>().HaveConversion<TestObjectId.EfCoreValueConverter>();
        configurationBuilder.Properties<TestParentId>().HaveConversion<TestParentId.EfCoreValueConverter>();
        configurationBuilder.Properties<TestUserId>().HaveConversion<TestUserId.EfCoreValueConverter>();
        configurationBuilder.Properties<TestGrantId>().HaveConversion<TestGrantId.EfCoreValueConverter>();
    }

    /// <summary>
    ///     Creates a context over a fresh, open in-memory SQLite connection. The caller owns the returned
    ///     connection and must dispose it (disposing the connection drops the in-memory database).
    /// </summary>
    public static (TestDbContext Context, SqliteConnection Connection) CreateSqlite() {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var context = CreateSqliteOn(connection);
        context.Database.EnsureCreated();
        return (context, connection);
    }

    /// <summary>
    ///     Creates an additional context over an existing SQLite connection (sharing the same
    ///     in-memory database) — e.g. to simulate a second replica competing for the drainer lease.
    /// </summary>
    public static TestDbContext CreateSqliteOn(SqliteConnection connection) {
        var options = new DbContextOptionsBuilder<TestDbContext>()
                     .UseSqlite(connection)
                     .Options;
        return new TestDbContext(options);
    }

    /// <summary>Creates a context on the InMemory provider (for the non-relational code paths).</summary>
    public static TestDbContext CreateInMemory() {
        var options = new DbContextOptionsBuilder<TestDbContext>()
                     .UseInMemoryDatabase(System.Guid.NewGuid().ToString())
                     .Options;
        return new TestDbContext(options);
    }
}
