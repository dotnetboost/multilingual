using DotNetBoost.MultiLingual.Core;
using DotNetBoost.MultiLingual.Core.Metadata;
using DotNetBoost.MultiLingual.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetBoost.MultiLingual.EntityFrameworkCore;

/// <summary>
/// Implement this on your <see cref="DbContext"/> to let the EF Core provider use it, and call
/// <see cref="MultiLingualModelBuilderExtensions.ApplyMultiLingual(ModelBuilder, DbContext, Action{MultiLingualModelOptions})"/>
/// at the end of
/// <c>OnModelCreating</c>.
/// </summary>
public interface IMultiLingualDbContext
{
    /// <summary>The translation rows backing every <c>[MultiLingual]</c> entity.</summary>
    DbSet<Translation> Translations { get; }

    /// <summary>The languages content can be served in.</summary>
    DbSet<Language> Languages { get; }
}

/// <summary>Table naming for the two tables this provider adds to your model.</summary>
public sealed class MultiLingualModelOptions
{
    /// <summary>Table holding translation rows. Default: <c>Translations</c>.</summary>
    public string TranslationsTable { get; set; } = "Translations";

    /// <summary>Table holding languages. Default: <c>Languages</c>.</summary>
    public string LanguagesTable { get; set; } = "Languages";

    /// <summary>Schema for both tables. Default: the model's default schema.</summary>
    public string? Schema { get; set; }
}

/// <summary>Entity mapping for <see cref="Translation"/>.</summary>
/// <param name="options">Table naming.</param>
public sealed class TranslationConfiguration(MultiLingualModelOptions options) : IEntityTypeConfiguration<Translation>
{
    /// <inheritdoc/>
    public void Configure(EntityTypeBuilder<Translation> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(options.TranslationsTable, options.Schema);

        // The natural key. Its leading columns (EntityName, EntityId) are exactly what every read
        // filters on, so the primary key index doubles as the read-path index.
        builder.HasKey(t => new { t.EntityName, t.EntityId, t.PropertyName, t.LanguageCode })
            .HasName("PK_" + options.TranslationsTable);

        builder.Property(t => t.EntityName).HasMaxLength(MultiLingualMetadata.MaxEntityNameLength).IsRequired();

        // 64 comfortably fits an int, a long, or a Guid in "D" format (36 chars).
        builder.Property(t => t.EntityId).HasMaxLength(EntityKey.MaxLength).IsRequired();
        builder.Property(t => t.PropertyName).HasMaxLength(128).IsRequired();
        builder.Property(t => t.LanguageCode).HasMaxLength(LanguageCodes.MaxLength).IsRequired();

        // No length cap: nvarchar(max) / text / TEXT. Descriptions get long.
        builder.Property(t => t.Value);

        builder.HasOne<Language>()
            .WithMany()
            .HasForeignKey(t => t.LanguageCode)
            // Deleting a language that still has translations is BLOCKED at the database as well
            // as in ILanguageManager, so a mistake cannot silently destroy translated content.
            .OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>Entity mapping for <see cref="Language"/>.</summary>
/// <param name="options">Table naming.</param>
public sealed class LanguageConfiguration(MultiLingualModelOptions options) : IEntityTypeConfiguration<Language>
{
    /// <inheritdoc/>
    public void Configure(EntityTypeBuilder<Language> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(options.LanguagesTable, options.Schema);

        builder.HasKey(l => l.Code);
        builder.Property(l => l.Code).HasMaxLength(LanguageCodes.MaxLength).ValueGeneratedNever();
        builder.Property(l => l.Name).HasMaxLength(128).IsRequired();

        // Stored as "Ltr"/"Rtl" rather than 0/1 so the column reads sensibly in the database.
        builder.Property(l => l.Direction).HasConversion<string>().HasMaxLength(3).IsRequired();

        builder.Property(l => l.IsDefault).IsRequired();
        builder.Property(l => l.IsActive).IsRequired();

        // Non-unique on purpose. A UNIQUE filtered index on IsDefault looks tempting, but
        // promoting a new default means one row goes true and another goes false in the same
        // SaveChanges, and EF does not guarantee the order of statements inside a batch — the
        // index would intermittently reject a perfectly valid swap.
        builder.HasIndex(l => l.IsDefault);
    }
}

/// <summary>Model-building entry points.</summary>
public static class MultiLingualModelBuilderExtensions
{
    /// <summary>
    /// Adds the <c>Translations</c> and <c>Languages</c> tables, and removes every translatable
    /// property from its owning entity's table mapping.
    /// <para>
    /// Call this at the END of <c>OnModelCreating</c>, after <c>ApplyConfigurationsFromAssembly</c>,
    /// so that entity types discovered through configurations and navigations are covered too.
    /// </para>
    /// </summary>
    public static ModelBuilder ApplyMultiLingual(
        this ModelBuilder modelBuilder,
        Action<MultiLingualModelOptions>? configure = null) =>
        modelBuilder.ApplyMultiLingual(new MultiLingualOptions(), configure);

    /// <summary>
    /// As <see cref="ApplyMultiLingual(ModelBuilder, Action{MultiLingualModelOptions})"/>, reading
    /// the engine's configuration — most importantly <see cref="MultiLingualOptions.BaseLanguage"/> —
    /// from <paramref name="context"/>'s application services. Pass <c>this</c>:
    /// <code>
    /// protected override void OnModelCreating(ModelBuilder modelBuilder)
    /// {
    ///     modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    ///     modelBuilder.ApplyMultiLingual(this);
    /// }
    /// </code>
    /// <para>
    /// Use this overload whenever <c>BaseLanguage</c> is configured, so the mapping and the engine
    /// cannot disagree about where the base language lives.
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The engine's options cannot be reached from <paramref name="context"/> — typically a
    /// design-time context built without the application's DI. Pass the options explicitly.
    /// </exception>
    public static ModelBuilder ApplyMultiLingual(
        this ModelBuilder modelBuilder,
        DbContext context,
        Action<MultiLingualModelOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        return modelBuilder.ApplyMultiLingual(ResolveEngineOptions(context), configure);
    }

    /// <summary>
    /// As <see cref="ApplyMultiLingual(ModelBuilder, Action{MultiLingualModelOptions})"/>, with the
    /// engine's options supplied directly. For design-time contexts that have no application
    /// services: the values here must match what <c>AddMultiLingual</c> configures, or the
    /// generated migration will not match the mapping the engine expects.
    /// </summary>
    public static ModelBuilder ApplyMultiLingual(
        this ModelBuilder modelBuilder,
        MultiLingualOptions engineOptions,
        Action<MultiLingualModelOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentNullException.ThrowIfNull(engineOptions);

        var options = new MultiLingualModelOptions();
        configure?.Invoke(options);

        modelBuilder.ApplyConfiguration(new LanguageConfiguration(options));
        modelBuilder.ApplyConfiguration(new TranslationConfiguration(options));

        return modelBuilder.IgnoreMultiLingualProperties(engineOptions);
    }

    private static MultiLingualOptions ResolveEngineOptions(DbContext context)
    {
        var applicationServices = context.GetService<IDbContextOptions>()
            .FindExtension<CoreOptionsExtension>()?.ApplicationServiceProvider;

        return applicationServices?.GetService<MultiLingualOptions>()
               ?? throw new InvalidOperationException(
                   $"ApplyMultiLingual({context.GetType().Name}) cannot reach the MultiLingual options: " +
                   "this context was built without the application's service provider. Register it with " +
                   "AddDbContext((sp, options) => options.UseMultiLingual(sp)), or call the overload that " +
                   "takes MultiLingualOptions directly. Guessing here would silently map the wrong " +
                   "columns and, at design time, generate a migration that drops them.");
    }

    /// <summary>
    /// Removes every translatable property from its owning entity's table mapping — the value
    /// lives in the translations table, never in a column on the entity's own. Included in
    /// <see cref="ApplyMultiLingual(ModelBuilder, Action{MultiLingualModelOptions})"/>; call it
    /// separately only if you map the two tables yourself.
    /// </summary>
    public static ModelBuilder IgnoreMultiLingualProperties(this ModelBuilder modelBuilder) =>
        modelBuilder.IgnoreMultiLingualProperties(new MultiLingualOptions());

    /// <summary>
    /// As <see cref="IgnoreMultiLingualProperties(ModelBuilder)"/>, honouring
    /// <see cref="MultiLingualOptions.BaseLanguage"/>: with a base language configured, translatable
    /// properties KEEP their column — it is where that language's value is stored — and only the
    /// other languages become translation rows.
    /// <para>
    /// Also validates every <c>[MultiLingual]</c> entity in the model — declarations and entity
    /// names — as <c>Build()</c> does for public types. The model is the only place that sees
    /// internal entity types too, so two of them sharing an entity name fail here, when the model
    /// is first built, rather than reading and overwriting each other's rows.
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">A declaration is invalid, or two entity types share an entity name.</exception>
    public static ModelBuilder IgnoreMultiLingualProperties(
        this ModelBuilder modelBuilder,
        MultiLingualOptions engineOptions)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentNullException.ThrowIfNull(engineOptions);

        MultiLingualModelValidator.Validate(modelBuilder.Model.GetEntityTypes().Select(e => e.ClrType).Distinct());

        foreach (var entityType in modelBuilder.Model.GetEntityTypes().ToList())
        {
            var clrType      = entityType.ClrType;
            var translatable = MultiLingualMetadata.For(clrType);
            if (translatable.Count == 0)
                continue;

            var keyPropertyNames = entityType.FindPrimaryKey()?.Properties
                .Select(p => p.Name)
                .ToHashSet(StringComparer.Ordinal) ?? [];

            var builder = modelBuilder.Entity(clrType);

            foreach (var property in translatable)
            {
                if (keyPropertyNames.Contains(property.Name))
                {
                    throw new InvalidOperationException(
                        $"'{clrType.Name}.{property.Name}' is part of the primary key and cannot be " +
                        "translatable. Remove [MultiLingual] from it, or mark it [NotMultiLingual] " +
                        "if the attribute was applied at class level.");
                }

                // Base-language mode: the column stays, holding the base language's value.
                if (engineOptions.HasBaseLanguage)
                    continue;

                builder.Ignore(property.Name);
            }
        }

        return modelBuilder;
    }
}
