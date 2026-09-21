using DotNetBoost.MultiLingual.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace DotNetBoost.MultiLingual.EntityFrameworkCore;

/// <summary>
/// The change tracker as it stood before a save — taken by <see cref="MultiLingualSaveChangesInterceptor"/>: every entry the save will change, and every
/// tracked translation row. <see cref="Restore"/> puts it back after a rolled-back save.
/// </summary>
internal sealed class TrackerSnapshot
{
    private readonly List<EntrySnapshot> _entries;
    private readonly HashSet<object> _translations;

    private TrackerSnapshot(List<EntrySnapshot> entries, HashSet<object> translations)
    {
        _entries      = entries;
        _translations = translations;
    }

    public static TrackerSnapshot Take(IEnumerable<EntityEntry> entries)
    {
        var changed      = new List<EntrySnapshot>();
        var translations = new HashSet<object>(ReferenceEqualityComparer.Instance);

        foreach (var entry in entries)
        {
            if (entry.Entity is Translation)
                translations.Add(entry.Entity);

            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
                continue;

            changed.Add(new EntrySnapshot(
                entry,
                entry.State,
                entry.CurrentValues.Clone(),
                entry.State == EntityState.Added ? null : entry.OriginalValues.Clone(),
                entry.Properties.Where(p => p.IsModified).Select(p => p.Metadata.Name).ToHashSet(StringComparer.Ordinal),
                entry.Properties.Where(p => p.IsTemporary).Select(p => p.Metadata.Name).ToArray()));
        }

        return new TrackerSnapshot(changed, translations);
    }

    public void Restore(DbContext context)
    {
        // Translation rows the failed write started tracking would collide with the retry's.
        foreach (var entry in context.ChangeTracker.Entries<Translation>().ToList())
        {
            if (!_translations.Contains(entry.Entity))
                entry.State = EntityState.Detached;
        }

        // States first: deleted entries are detached by now, and must be tracked again before
        // their values can be set.
        foreach (var saved in _entries)
        {
            if (saved.Entry.State != saved.State)
                saved.Entry.State = saved.State;
        }

        // Then the temporary keys of added entries, principals and dependents alike, before any
        // other value. Fix-up carries a principal's key into its dependents' foreign keys, so
        // restoring keys first keeps every relationship intact.
        foreach (var saved in _entries.Where(e => e.State == EntityState.Added))
        {
            foreach (var name in saved.Temporary)
            {
                var property = saved.Entry.Property(name);
                if (property.Metadata.IsPrimaryKey())
                    property.CurrentValue = saved.Current[name];
            }
        }

        foreach (var saved in _entries)
        {
            foreach (var property in saved.Entry.Properties)
            {
                var metadata = property.Metadata;

                // Keys of tracked entries cannot change, and added ones were restored above.
                if (metadata.IsPrimaryKey())
                    continue;

                var current = saved.Current[metadata];
                if (!Equals(property.CurrentValue, current))
                    property.CurrentValue = current;

                if (saved.Original is { } original)
                {
                    var value = original[metadata];
                    if (!Equals(property.OriginalValue, value))
                        property.OriginalValue = value;
                }

                if (saved.State == EntityState.Modified)
                    property.IsModified = saved.Modified.Contains(metadata.Name);
            }

            if (saved.State == EntityState.Added)
            {
                foreach (var name in saved.Temporary)
                    saved.Entry.Property(name).IsTemporary = true;
            }
        }
    }
}

internal sealed record EntrySnapshot(
    EntityEntry Entry,
    EntityState State,
    PropertyValues Current,
    PropertyValues? Original,
    HashSet<string> Modified,
    string[] Temporary);
