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
    public DbSet<TestReassignableEntity> Reassignables => Set<TestReassignableEntity>();
    public DbSet<SealedFgaOutboxEntry> Outbox => Set<SealedFgaOutboxEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) {
        base.OnModelCreating(modelBuilder);
        // Surrogate key so the SealedFGA Id stays a plain mutable property (see TestReassignableEntity).
        modelBuilder.Entity<TestReassignableEntity>().HasKey(e => e.Pk);
        // Register the outbox entity the same way SealedFgaModelCustomizer does for a real consumer.
        modelBuilder.ConfigureSealedFgaOutbox();
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) {
        base.ConfigureConventions(configurationBuilder);
        configurationBuilder.Properties<TestObjectId>().HaveConversion<TestObjectId.EfCoreValueConverter>();
        configurationBuilder.Properties<TestParentId>().HaveConversion<TestParentId.EfCoreValueConverter>();
        configurationBuilder.Properties<TestUserId>().HaveConversion<TestUserId.EfCoreValueConverter>();
    }

    /// <summary>
    ///     Creates a context over a fresh, open in-memory SQLite connection. The caller owns the returned
    ///     connection and must dispose it (disposing the connection drops the in-memory database).
    /// </summary>
    public static (TestDbContext Context, SqliteConnection Connection) CreateSqlite() {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<TestDbContext>()
                     .UseSqlite(connection)
                     .Options;
        var context = new TestDbContext(options);
        context.Database.EnsureCreated();
        return (context, connection);
    }
}
