namespace Jularr.Web.Features.Collections;

/// <summary>
/// Whether a collection's membership is curated by hand or derived from a rule (#427). A
/// <see cref="Manual"/> collection holds exactly the works its owner added, in the order they were
/// arranged; a <see cref="Smart"/> collection stores a rule tree and its membership is recomputed
/// (materialized) from canonical media facts, so it stays current as the library changes.
/// </summary>
public enum CollectionKind
{
    Manual,
    Smart
}

/// <summary>Why a work is a member of a collection: added by hand, or matched by a smart rule.</summary>
public enum CollectionItemSource
{
    Manual,
    Rule
}

/// <summary>
/// A user-curated (<see cref="CollectionKind.Manual"/>) or rule-driven (<see cref="CollectionKind.Smart"/>)
/// shelf of cross-media <see cref="Jularr.Web.Features.MediaCore.Work"/>s (#427). A smart collection keeps
/// its rule tree in <see cref="RuleJson"/> (a nested ALL/ANY expression over canonical media facts and
/// franchise relations); a manual one has none. Membership itself lives in <see cref="CollectionItem"/> so
/// both kinds render identically and a smart collection's matches are explainable and cacheable after one
/// materialization pass.
/// </summary>
public sealed class Collection
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The profile that owns and curates the collection.</summary>
    public string ProfileId { get; set; } = "";

    public CollectionKind Kind { get; set; }

    public string Name { get; set; } = "";

    public string? Description { get; set; }

    /// <summary>Serialized nested ALL/ANY rule tree for a smart collection; null for a manual one.</summary>
    public string? RuleJson { get; set; }

    /// <summary>Display order of the collection among the profile's collections (lower first).</summary>
    public int SortOrder { get; set; }

    /// <summary>When a smart collection's membership was last recomputed; null before the first pass.</summary>
    public DateTime? LastMaterializedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One work's membership in a <see cref="Collection"/> (#427). For a manual collection the owner sets
/// <see cref="Position"/> to arrange the shelf; for a smart collection the materializer writes the rows,
/// records <see cref="MatchReason"/> (the human-readable "why it matched") and orders by the work's own
/// order. Unique per (collection, work) so a work never appears twice on one shelf.
/// </summary>
public sealed class CollectionItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid CollectionId { get; set; }

    public long WorkId { get; set; }

    public CollectionItemSource Source { get; set; }

    /// <summary>Manual arrangement index; for smart items the materialized rank.</summary>
    public int Position { get; set; }

    /// <summary>For a smart item: the explainable reasons the work matched, one per line. Empty otherwise.</summary>
    public string? MatchReason { get; set; }

    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
}
