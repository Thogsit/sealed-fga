using OpenFga.Sdk.Client.Model;
using OpenFga.Sdk.Model;
using SealedFga.Exceptions;
using SealedFga.Fga;
using Shouldly;
using Xunit;

namespace SealedFga.Tests;

/// <summary>
///     Unit tests for <see cref="SealedFgaService.MapBatchCheckResults" /> (exposed via
///     InternalsVisibleTo). A batch-check response that does not cover every requested
///     correlation ID must throw instead of silently defaulting the missing slots to false.
/// </summary>
public class BatchCheckMappingTests {
    private static ClientBatchCheckSingleResponse Item(string correlationId, bool allowed)
        => new() {
            CorrelationId = correlationId,
            Allowed = allowed,
        };

    [Fact]
    public void Complete_response_maps_results_by_correlation_id() {
        var results = SealedFgaService.MapBatchCheckResults(
            [Item("2", true), Item("0", true), Item("1", false)],
            3
        );

        results.ShouldBe([true, false, true]);
    }

    [Fact]
    public void Empty_request_yields_empty_results() {
        SealedFgaService.MapBatchCheckResults([], 0).ShouldBeEmpty();
    }

    [Fact]
    public void Missing_correlation_id_throws() {
        var ex = Should.Throw<FgaBatchCheckException>(() =>
            SealedFgaService.MapBatchCheckResults([Item("0", true), Item("2", true)], 3)
        );

        ex.Message.ShouldContain("incomplete");
        ex.Message.ShouldContain("1");
    }

    [Fact]
    public void Duplicate_correlation_id_throws() {
        Should.Throw<FgaBatchCheckException>(() =>
                  SealedFgaService.MapBatchCheckResults([Item("0", true), Item("0", true)], 2)
              )
              .Message.ShouldContain("duplicate");
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("-1")]
    [InlineData("2")]
    public void Unknown_correlation_id_throws(string correlationId) {
        Should.Throw<FgaBatchCheckException>(() =>
                  SealedFgaService.MapBatchCheckResults([Item("0", true), Item(correlationId, true)], 2)
              )
              .Message.ShouldContain("unknown correlation ID");
    }

    [Fact]
    public void Per_item_error_throws_instead_of_reading_as_not_allowed() {
        var errored = new ClientBatchCheckSingleResponse {
            CorrelationId = "0",
            Allowed = false,
            Request = new ClientBatchCheckItem { User = "user:1", Relation = "can_view", Object = "secret:1" },
            Error = new CheckError(message: "boom"),
        };

        Should.Throw<FgaBatchCheckException>(() =>
                  SealedFgaService.MapBatchCheckResults([errored], 1)
              )
              .Message.ShouldContain("boom");
    }
}
