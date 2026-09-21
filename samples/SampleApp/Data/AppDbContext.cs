using DotNetBoost.MultiLingual.Core.Models;
using DotNetBoost.MultiLingual.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SampleApp.Domain;

namespace SampleApp.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options), IMultiLingualDbContext
{
    public DbSet<Language> Languages => Set<Language>();

    public DbSet<Translation> Translations => Set<Translation>();

    public DbSet<Product> Products => Set<Product>();

    public DbSet<Category> Categories => Set<Category>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        // Runs LAST so that entity types discovered through configurations and navigations are
        // covered as well. Adds the Translations and Languages tables and, in the default mode,
        // removes every translatable property from its owner's table mapping.
        //
        // `this` is what lets it read BaseLanguage from the application's services: with one
        // configured, translatable properties KEEP their column instead, because that column is
        // where the base language lives. Passing the context is how the mapping and the engine
        // are kept from disagreeing — and a disagreement is caught on the first save.
        modelBuilder.ApplyMultiLingual(this);
    }
}

public sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("Products");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Sku).IsRequired().HasMaxLength(64);
        builder.Property(p => p.Price).HasPrecision(18, 2);
        builder.HasIndex(p => p.Sku).IsUnique();

        builder.HasOne(p => p.Category)
            .WithMany(c => c.Products)
            .HasForeignKey(p => p.CategoryId)
            .OnDelete(DeleteBehavior.SetNull);

        // Name and Description are translatable. With BaseLanguage set they are real columns
        // holding English, so they are configured like any other — drop these two lines and the
        // model check will tell you, on the first save, that the mapping no longer matches.
        // Without a base language they would have no column at all and configuring them throws.
        builder.Property(p => p.Name).HasMaxLength(256);
        builder.Property(p => p.Description).HasMaxLength(2048);
    }
}

public sealed class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> builder)
    {
        builder.ToTable("Categories");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Slug).IsRequired().HasMaxLength(128);
        builder.HasIndex(c => c.Slug).IsUnique();
        builder.Property(c => c.DisplayOrder).IsRequired();

        // Translatable, and therefore columns holding the base language. See ProductConfiguration.
        builder.Property(c => c.Name).HasMaxLength(256);
        builder.Property(c => c.Description).HasMaxLength(2048);
    }
}
