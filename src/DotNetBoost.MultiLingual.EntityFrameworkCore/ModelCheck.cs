using DotNetBoost.MultiLingual.Core;
using DotNetBoost.MultiLingual.Core.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace DotNetBoost.MultiLingual.EntityFrameworkCore;

/// <summary>
/// Verifies that the model and the engine agree about where each translatable property lives.
///
/// <para>
/// The two are configured in different places — <c>AddMultiLingual</c> sets
/// <see cref="MultiLingualOptions.BaseLanguage"/>, <c>ApplyMultiLingual</c> decides the mapping —
/// and disagreeing is silently destructive in both directions. A mapped property the engine
/// thinks is translation-only gets the authored language written into a column meant for the base
/// language; an ignored property the engine thinks is a column loses that value entirely. Neither
/// throws on its own, so it is checked here, once per entity type per model.
/// </para>
/// </summary>
internal static class MultiLingualModelCheck
{
    private static readonly ConditionalWeakTable<IModel, ConcurrentDictionary<Type, bool>> Verified = new();

    /// <summary>Checks <paramref name="clrType"/> against <paramref name="options"/>, once per model.</summary>
    /// <exception cref="InvalidOperationException">The mapping does not match the configured mode.</exception>
    public static void Verify(DbContext context, Type clrType, MultiLingualOptions options)
    {
        var perModel = Verified.GetValue(context.Model, static _ => new ConcurrentDictionary<Type, bool>());
        if (perModel.ContainsKey(clrType))
            return;

        if (context.Model.FindEntityType(clrType) is { } entityType)
        {
            foreach (var property in MultiLingualMetadata.For(clrType))
                VerifyProperty(context, entityType, clrType, property.Name, options);
        }

        perModel[clrType] = true;
    }

    private static void VerifyProperty(
        DbContext context,
        IEntityType entityType,
        Type clrType,
        string propertyName,
        MultiLingualOptions options)
    {
        var isMapped = entityType.FindProperty(propertyName) is not null;

        if (options.HasBaseLanguage && !isMapped)
        {
            throw new InvalidOperationException(
                $"BaseLanguage is '{options.BaseLanguage}', so '{clrType.Name}.{propertyName}' must keep " +
                $"its column — but '{context.GetType().Name}' does not map it. Call " +
                "modelBuilder.ApplyMultiLingual(this) in OnModelCreating so the mapping follows the " +
                "engine's configuration.");
        }

        if (!options.HasBaseLanguage && isMapped)
        {
            throw new InvalidOperationException(
                $"'{clrType.Name}.{propertyName}' is translatable, so its value belongs in the " +
                $"translations table — but '{context.GetType().Name}' still maps it to a column. Call " +
                "modelBuilder.ApplyMultiLingual() at the end of OnModelCreating, or set BaseLanguage if " +
                "the column is meant to hold one language.");
        }
    }
}
