using System;
using System.Collections.Immutable;
using System.Linq;
using OpenFga.Language.Model;

namespace SealedFga.Models;

internal record struct ModelFgaIncrementalChange {
    public string FilePath { get; set; }
    public AuthorizationModel? AuthorizationModel { get; set; }
    public ImmutableArray<ParseErrorInfo> ParseErrors { get; set; }

    // ImmutableArray<T> only compares the underlying array reference, so the synthesized record
    // equality would consider equal error lists different and needlessly re-run the pipeline.
    public readonly bool Equals(ModelFgaIncrementalChange other)
        => FilePath == other.FilePath
           && Equals(AuthorizationModel, other.AuthorizationModel)
           && (ParseErrors.IsDefaultOrEmpty
               ? other.ParseErrors.IsDefaultOrEmpty
               : !other.ParseErrors.IsDefaultOrEmpty && ParseErrors.SequenceEqual(other.ParseErrors));

    public override readonly int GetHashCode() {
        unchecked {
            var hash = FilePath?.GetHashCode() ?? 0;
            hash = (hash * 397) ^ (AuthorizationModel?.GetHashCode() ?? 0);
            if (!ParseErrors.IsDefaultOrEmpty) {
                hash = ParseErrors.Aggregate(hash, (current, error) => (current * 397) ^ error.GetHashCode());
            }

            return hash;
        }
    }
}
