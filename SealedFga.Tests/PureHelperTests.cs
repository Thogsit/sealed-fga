using System;
using System.Collections.Generic;
using System.Linq;
using SealedFga.Fga;
using SealedFga.Fga.Outbox;
using Shouldly;
using Xunit;
using TupleKey = OpenFga.Sdk.Model.TupleKey;

namespace SealedFga.Tests;

/// <summary>Unit tests for the deterministic, side-effect-free helpers (exposed via InternalsVisibleTo).</summary>
public class PureHelperTests {
    [Fact]
    public void DistinctTuples_dedupes_by_user_relation_object() {
        var tuples = new List<TupleKey> {
            new() { User = "a:1", Relation = "r", Object = "b:1" },
            new() { User = "a:1", Relation = "r", Object = "b:1" }, // duplicate
            new() { User = "a:1", Relation = "r", Object = "b:2" }, // distinct object
        };

        var result = SealedFgaService.DistinctTuples(tuples);

        result.Count.ShouldBe(2);
        result.ShouldContain(t => t.Object == "b:1");
        result.ShouldContain(t => t.Object == "b:2");
    }

    [Theory]
    [InlineData(0, 1)]     // 2^0
    [InlineData(1, 2)]     // 2^1
    [InlineData(3, 8)]     // 2^3
    [InlineData(8, 256)]   // 2^8
    [InlineData(10, 300)]  // 2^10 = 1024, capped at 300
    [InlineData(20, 300)]  // well past the cap
    public void ComputeBackoff_is_exponential_and_capped_at_300s(int attempts, double expectedSeconds)
        => SealedFgaOutboxDrainer.ComputeBackoff(attempts).TotalSeconds.ShouldBe(expectedSeconds);
}
