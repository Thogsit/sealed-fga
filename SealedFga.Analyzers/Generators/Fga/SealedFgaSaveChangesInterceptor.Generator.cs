using System.Collections.Generic;
using SealedFga.Models;

namespace SealedFga.Generators.Fga;

internal static class SealedFgaSaveChangesInterceptorGenerator {
    public static GeneratedFile Generate()
        => new(
            "SealedFgaSaveChangesInterceptor.g.cs",
            """
            /// <summary>
            ///     EF Core <see cref="SaveChangesInterceptor" /> that translates tracked entity changes
            ///     — relation attributes and <c>ISealedFgaTupleSource</c> diffs alike — into SealedFGA
            ///     outbox entries in the same transaction (via
            ///     <see cref="SealedFgaSaveChangesProcessor" />). Registered by
            ///     <c>ConfigureSealedFga</c> and attached via <c>DbContextOptionsBuilder.AddSealedFga</c>.
            /// </summary>
            public class SealedFgaSaveChangesInterceptor : SaveChangesInterceptor
            {
                private static readonly ThreadLocal<bool> IsProcessing = new();

                /// <summary>
                ///     Wrapper around <see cref="SealedFgaSaveChangesProcessor.ProcessSealedFgaChanges" />
                ///     that ensures that it is not called recursively.
                ///     This can e.g. happen due to the TickerQ usage for SealedFGA change tracking.
                /// </summary>
                /// <param name="context">The context whose tracked changes are processed.</param>
                private void RecursionSafeProcessSealedFgaChanges(DbContext? context) {
                    if (IsProcessing.Value) {
                        return;
                    }

                    try {
                        IsProcessing.Value = true;
                        var processor = new SealedFgaSaveChangesProcessor();
                        processor.ProcessSealedFgaChanges(context);
                    } finally {
                        IsProcessing.Value = false;
                    }
                }

                /// <inheritdoc />
                public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
                    DbContextEventData eventData,
                    InterceptionResult<int> result,
                    CancellationToken cancellationToken = new()
                ) {
                    RecursionSafeProcessSealedFgaChanges(eventData.Context);
                    return base.SavingChangesAsync(eventData, result, cancellationToken);
                }

                /// <inheritdoc />
                public override InterceptionResult<int> SavingChanges(
                    DbContextEventData eventData,
                    InterceptionResult<int> result
                ) {
                    RecursionSafeProcessSealedFgaChanges(eventData.Context);
                    return base.SavingChanges(eventData, result);
                }
            }
            """,
            new HashSet<string>([
                    "System",
                    "System.Threading",
                    "System.Threading.Tasks",
                    "Microsoft.EntityFrameworkCore",
                    "Microsoft.EntityFrameworkCore.Diagnostics",
                ]
            ),
            Settings.FgaNamespace
        );
}
