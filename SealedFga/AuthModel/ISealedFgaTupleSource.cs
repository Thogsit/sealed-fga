using System.Collections.Generic;
using SealedFga.Fga;

namespace SealedFga.AuthModel;

/// <summary>
///     Declares an entity whose OpenFGA tuples are a <b>pure function of its row values</b>. The
///     SealedFGA SaveChanges interceptor evaluates <see cref="DesiredTuples" /> for every tracked
///     change of the entity and enqueues the <i>difference</i> into the transactional outbox — in the
///     same DB transaction as the row change itself:
///     <list type="bullet">
///         <item><c>Added</c> → writes the desired tuples of the new row.</item>
///         <item>
///             <c>Modified</c> → diffs the desired tuples of the original row values against those of
///             the current values; the added set is written, the removed set is deleted.
///         </item>
///         <item><c>Deleted</c> → deletes the desired tuples of the original row values.</item>
///     </list>
///     This makes the tuples a declarative projection of the row: state machines whose rows are never
///     hard-deleted (grants, memberships with lifecycle states) simply return an empty sequence for
///     inactive states, and a forgotten tuple write at some call site becomes structurally impossible.
///     <para>
///         <b>Purity contract</b>: the implementation must depend only on the entity's mapped,
///         non-navigation properties — no database or service access, no navigations, no ambient
///         state (static helpers such as a permission catalog are fine). The interceptor relies on
///         this to evaluate the original row via a detached instance materialized from EF's original
///         property values.
///     </para>
///     <para>
///         <b>Composition</b>: an entity implementing this interface must not additionally declare
///         <c>[SealedFgaRelation]</c> / <c>[SealedFgaJoinRelation]</c> — the tuple source owns
///         <i>all</i> of the entity's tuples (diagnostic <c>SFGA004</c>, plus a runtime check).
///         Implementing <see cref="ISealedFgaType{TId}" /> alongside is fine and typical.
///         Unlike attribute-annotated entities, deleting a tuple-source entity enqueues no
///         <c>DeleteAllForObject</c> purge fence: the diff is exhaustive by construction, and desired
///         tuples may not reference the row's own id on either side (e.g. permission fan-outs), which
///         no id-keyed fence could ever clean up.
///     </para>
///     <para>
///         <b>Limitation</b>: changes that bypass the EF change tracker (<c>ExecuteUpdate</c> /
///         <c>ExecuteDelete</c> / raw SQL) are invisible to the interceptor and sync nothing.
///         Mutate tuple-source rows only through tracked entities, and own a periodic reconciliation
///         backstop if such bypasses can occur.
///     </para>
/// </summary>
public interface ISealedFgaTupleSource {
    /// <summary>
    ///     Returns the complete set of OpenFGA tuples that should exist for this row's <b>current</b>
    ///     property values. Must be pure over the entity's mapped, non-navigation properties (see the
    ///     interface docs for the full contract). Duplicate operations are tolerated and deduplicated.
    /// </summary>
    /// <returns>The desired tuple operations; empty when no tuples should exist for this state.</returns>
    IEnumerable<SealedFgaTupleOperation> DesiredTuples();
}
