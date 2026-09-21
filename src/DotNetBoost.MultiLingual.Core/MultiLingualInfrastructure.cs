using DotNetBoost.MultiLingual.Core.Metadata;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using System.Text;

namespace DotNetBoost.MultiLingual.Core;

/// <summary>Fluent builder returned by <c>services.AddMultiLingual()</c>.</summary>
public sealed class MultiLingualBuilder
{
    /// <summary>The service collection being configured. Providers add their registrations here.</summary>
    public IServiceCollection Services { get; }

    internal bool    ProviderConfigured { get; set; }
    internal string? ProviderName       { get; set; }

    /// <summary>
    /// The options instance handed to the engine through DI. Builder methods mutate this object
    /// rather than a private copy, so configuration applied anywhere in the chain takes effect.
    /// </summary>
    internal MultiLingualOptions Options { get; }

    /// <summary>Creates a builder over <paramref name="services"/> and registers its options.</summary>
    public MultiLingualBuilder(IServiceCollection services)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
        Options  = new MultiLingualOptions();
        Services.AddSingleton(Options);
    }

    /// <summary>
    /// Validates the builder configuration and every <c>[MultiLingual]</c> type in the loaded
    /// assemblies, then returns the service collection. Call once at the end of the chain.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// No provider is configured, or a <c>[MultiLingual]</c> declaration cannot work.
    /// </exception>
    public IServiceCollection Build()
    {
        if (!ProviderConfigured)
        {
            throw new InvalidOperationException(
                "No MultiLingual provider configured. Call UseEntityFrameworkCore<TContext>() " +
                "or UseCustomStores<TTranslationStore, TLanguageStore>() first.");
        }

        MultiLingualModelValidator.Validate(AppDomain.CurrentDomain.GetAssemblies());

        // Base-language mode has one precondition the model validator cannot see, because it
        // lives in the database rather than in the code: the base language has to exist.
        if (Options is { HasBaseLanguage: true, ValidateBaseLanguageAtStartup: true })
            Services.AddHostedService<MultiLingualValidationHostedService>();

        return Services;
    }
}

/// <summary>Guards against configuring more than one storage provider on a builder.</summary>
public static class MultiLingualBuilderGuard
{
    /// <summary>Records <paramref name="providerName"/> as the builder's provider.</summary>
    /// <exception cref="InvalidOperationException">A provider is already configured.</exception>
    public static void EnsureProviderNotConfigured(MultiLingualBuilder builder, string providerName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);

        if (builder.ProviderConfigured)
        {
            throw new InvalidOperationException(
                $"A MultiLingual provider ('{builder.ProviderName}') is already configured. " +
                $"Cannot also configure '{providerName}'.");
        }

        builder.ProviderConfigured = true;
        builder.ProviderName       = providerName;
    }
}

/// <summary>
/// Startup check for <c>[MultiLingual]</c> declarations. The metadata cache already rejects a
/// bad declaration — but only when the type is first used, which can be deep into a request.
/// Running it over every decorated type at startup turns that into a failed boot.
/// </summary>
public static class MultiLingualModelValidator
{
    /// <summary>
    /// Validates every public decorated type in <paramref name="assemblies"/>. Framework
    /// assemblies are skipped; internal types are checked when first used.
    /// </summary>
    /// <exception cref="InvalidOperationException">A declaration is invalid; the message lists every offender.</exception>
    public static void Validate(IEnumerable<Assembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        Validate(assemblies.Where(IsApplicationAssembly).SelectMany(SafeExportedTypes));
    }

    /// <summary>Validates the decorated types among <paramref name="types"/>.</summary>
    /// <exception cref="InvalidOperationException">A declaration is invalid; the message lists every offender.</exception>
    public static void Validate(IEnumerable<Type> types)
    {
        ArgumentNullException.ThrowIfNull(types);

        var errors = new List<string>();
        var named  = new List<(Type Type, string Name)>();

        foreach (var type in types.Where(t => t is { IsClass: true, IsAbstract: false }))
        {
            try
            {
                if (!MultiLingualMetadata.IsDecorated(type))
                    continue;

                if (MultiLingualMetadata.For(type).Count > 0)
                    named.Add((type, MultiLingualMetadata.EntityNameFor(type)));
            }
            catch (InvalidOperationException ex)
            {
                errors.Add(ex.Message);
            }
        }

        errors.AddRange(FindNameClashes(named));

        if (errors.Count == 0)
            return;

        var sb = new StringBuilder("Invalid [MultiLingual] declarations:");
        foreach (var error in errors)
            sb.AppendLine().Append("  - ").Append(error);

        throw new InvalidOperationException(sb.ToString());
    }

    /// <summary>
    /// Two unrelated types stored under one entity name would read and overwrite each other's
    /// translations. Compared case-insensitively, since a case-insensitive database collation
    /// matches the rows that way. A hierarchy sharing its base's name is one entity, not a clash.
    /// </summary>
    private static IEnumerable<string> FindNameClashes(List<(Type Type, string Name)> named)
    {
        foreach (var group in named.GroupBy(n => n.Name, StringComparer.OrdinalIgnoreCase))
        {
            var roots = group
                .Select(n => NamingRoot(n.Type, n.Name))
                .Distinct()
                .OrderBy(t => t.FullName, StringComparer.Ordinal)
                .ToList();

            if (roots.Count < 2)
                continue;

            var types = string.Join(", ", group
                .Select(n => $"{n.Type.FullName} (\"{n.Name}\")")
                .Order(StringComparer.Ordinal));

            yield return
                $"These types would share translation rows under the entity name '{group.Key}': {types}. " +
                "Give each a distinct name with [MultiLingualEntity(\"...\")].";
        }
    }

    /// <summary>The topmost ancestor of <paramref name="type"/> still stored under <paramref name="name"/>.</summary>
    private static Type NamingRoot(Type type, string name)
    {
        var root = type;
        while (root.BaseType is { } parent
               && parent != typeof(object)
               && string.Equals(MultiLingualMetadata.EntityNameFor(parent), name, StringComparison.OrdinalIgnoreCase))
        {
            root = parent;
        }

        return root;
    }

    private static bool IsApplicationAssembly(Assembly assembly)
    {
        if (assembly.IsDynamic)
            return false;

        var name = assembly.GetName().Name ?? string.Empty;
        return !name.StartsWith("System", StringComparison.Ordinal)
               && !name.StartsWith("Microsoft", StringComparison.Ordinal)
               && !name.StartsWith("netstandard", StringComparison.Ordinal)
               && !name.StartsWith("mscorlib", StringComparison.Ordinal);
    }

    private static IEnumerable<Type> SafeExportedTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetExportedTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is { IsPublic: true }).Cast<Type>();
        }
        catch (FileNotFoundException)
        {
            return [];
        }
        catch (NotSupportedException)
        {
            return [];
        }
    }
}
