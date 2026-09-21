using DotNetBoost.MultiLingual.Core;
using DotNetBoost.MultiLingual.Core.Attributes;
using DotNetBoost.MultiLingual.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace DotNetBoost.MultiLingual.ProviderTests.EfCore;

// Everything here is internal on purpose: Build() only scans public types, so these are exactly
// what it cannot see — and what the model check has to catch.

internal static class Sales
{
    internal sealed class Item
    {
        public int Id { get; set; }
        [MultiLingual] public string? Name { get; set; }
    }

    [MultiLingualEntity("sales-item")]
    internal sealed class NamedItem
    {
        public int Id { get; set; }
        [MultiLingual] public string? Name { get; set; }
    }
}

internal static class Library
{
    internal sealed class Item
    {
        public int Id { get; set; }
        [MultiLingual] public string? Name { get; set; }
    }

    internal sealed class BadItem
    {
        public int Id { get; set; }
        [MultiLingual] public int Pages { get; set; }
    }
}

internal sealed class ClashingDbContext(DbContextOptions<ClashingDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Sales.Item>().ToTable("SalesItems");
        modelBuilder.Entity<Library.Item>().ToTable("LibraryItems");
        modelBuilder.ApplyMultiLingual();
    }
}

internal sealed class DistinctNamesDbContext(DbContextOptions<DistinctNamesDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Sales.NamedItem>().ToTable("SalesItems");
        modelBuilder.Entity<Library.Item>().ToTable("LibraryItems");
        modelBuilder.ApplyMultiLingual();
    }
}

internal sealed class InvalidDeclarationDbContext(DbContextOptions<InvalidDeclarationDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Library.BadItem>();
        modelBuilder.ApplyMultiLingual();
    }
}

/// <summary>
/// The model is checked when it is built, which covers the internal entity types <c>Build()</c>
/// cannot see: an entity-name clash or a bad declaration fails at once, instead of two entities
/// quietly sharing translation rows.
/// </summary>
public sealed class ModelValidationTests
{
    private static TContext Create<TContext>(Func<DbContextOptions<TContext>, TContext> create)
        where TContext : DbContext =>
        create(new DbContextOptionsBuilder<TContext>().UseSqlite("DataSource=:memory:").Options);

    [Fact]
    public void StartupCheck_CannotSeeInternalTypes()
    {
        // This assembly holds the clashing internal types above, and Build()'s scan passes it.
        MultiLingualModelValidator.Validate([typeof(ModelValidationTests).Assembly]);
    }

    [Fact]
    public void InternalEntityTypes_SharingAName_FailWhenTheModelIsBuilt()
    {
        using var context = Create<ClashingDbContext>(o => new(o));

        var ex = Assert.Throws<InvalidOperationException>(() => context.Model);

        Assert.Contains("'Item'", ex.Message, StringComparison.Ordinal);
        Assert.Contains(typeof(Sales.Item).FullName!, ex.Message, StringComparison.Ordinal);
        Assert.Contains(typeof(Library.Item).FullName!, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InternalEntityTypes_WithDistinctNames_Build()
    {
        using var context = Create<DistinctNamesDbContext>(o => new(o));

        Assert.NotNull(context.Model.FindEntityType(typeof(Sales.NamedItem)));
    }

    [Fact]
    public void InternalEntityType_WithAnInvalidDeclaration_FailsWhenTheModelIsBuilt()
    {
        using var context = Create<InvalidDeclarationDbContext>(o => new(o));

        var ex = Assert.Throws<InvalidOperationException>(() => context.Model);

        Assert.Contains("Invalid [MultiLingual] declarations", ex.Message, StringComparison.Ordinal);
        Assert.Contains("BadItem.Pages", ex.Message, StringComparison.Ordinal);
    }
}
