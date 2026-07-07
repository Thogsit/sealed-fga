# SealedFga library — Agent Guide

This is the SealedFGA framework itself: a NuGet package that is **both a Roslyn source
generator and a runtime library**. Read the [root `AGENTS.md`](../AGENTS.md) first for the
high-level picture and the three pillars. This document explains how the library is built and
how each piece works.

## The two halves

Because a Roslyn generator must target `netstandard2.0` and can't reference the ASP.NET
Core / EF Core runtime types it emits against, the code is split:

- **Generator (compile-time)** — `SealedFgaSourceGenerator.cs` + everything under `Generators/`.
  Runs during the consumer's build, emits C# into the consumer's compilation.
- **Runtime library** — `Fga/`, `ModelBinder/`, `Middleware/`, `Exceptions/`, `AuthModel/`,
  `Util/`, `Models/`, `Attributes/`. Ordinary compiled code the generated glue wires up.

A concrete example of the split: `SealedFgaSaveChangesProcessor` (the sync logic) is
hand-written in `Fga/`, while the EF Core `SaveChangesInterceptor` that calls it is *generated*
into the consumer's assembly — because the interceptor must inherit an EF Core base type the
generator project cannot reference.

## Source generation

### Entry point — `SealedFgaSourceGenerator.cs`

An `[Generator]` implementing `IIncrementalGenerator`. In `Initialize` it wires two inputs and
combines them:

- **`model.fga`** via `context.AdditionalTextsProvider` (filtered to files named `model.fga`),
  parsed by the vendored `OpenFga.Language` ANTLR parser (`OpenFgaFromDslTransformer.ParseDsl`)
  into an `AuthorizationModel`.
- **`[SealedFgaTypeId]`-annotated classes** via `context.SyntaxProvider.ForAttributeWithMetadataName`,
  collected into an immutable array of `IdClassToGenerateData`.

Two diagnostics are reported:
- `SFGA001` — `model.fga` could not be parsed.
- `SFGA002` — a `[SealedFgaTypeId]` references an OpenFGA type name not present in `model.fga`.

Outputs:
- **Incremental** (`RegisterSourceOutput`): per annotated ID class, emits the typed-ID partial
  and its relation classes; plus `SealedFgaInit.g.cs`.
- **Post-init / non-incremental** (`RegisterPostInitializationOutput`): emits
  `SealedFgaExtensions.g.cs` and `SealedFgaSaveChangesInterceptor.g.cs` (they don't depend on
  user code).

### What gets generated (`Generators/`)

- **`Generators/AuthModel/$TypeName$Id.Generator.cs` → `<Name>.partial.g.cs`.** Completes the
  user's empty `partial class XId` into a strongly-typed ID: a `Value` of the backing type
  (`Guid` or `string`), a constructor, `static OpenFgaTypeName`, `static New()`, `static Parse(string)`,
  `AsOpenFgaIdTupleString()` (returns `"typename:value"`), value equality + `==`/`!=`, and three
  nested converters — `EfCoreValueConverter : ValueConverter<XId, Guid|string>` (DB persistence),
  `IdJsonConverter : JsonSimpleStringConverter<XId>` (JSON), `IdTypeConverter`
  (`GuidIdTypeConverter<>` or `StringIdTypeConverter<>`, for MVC/general conversion).

- **`Generators/AuthModel/$TypeName$Relations.Generator.cs` → `<Name>Attributes.g.cs` and/or
  `<Name>Groups.g.cs`.** Turns a type's relations into strongly-typed constants. **The split is
  by the first letter's casing of the relation name:**
  - **lowercase → `<Name>Attributes`** — permission-style relations (e.g. `can_view`, `can_edit`).
  - **uppercase → `<Name>Groups`** — grouping/parent relations (e.g. `OwnedBy`, `Member`).

  Each generated class derives from `SealedFgaRelation` and implements `ISealedFgaRelation<XId>`,
  with one `public static readonly` member per relation plus a `FromOpenFgaString` factory.

- **`Generators/SealedFgaExtensions.Generator.cs` → `SealedFgaExtensions.g.cs`.** The DI / EF /
  middleware wiring extension methods (see [DI API](#di--wiring-api)).

- **`Generators/SealedFgaInit.Generator.cs` → `SealedFgaInit.g.cs`.** A `SealedFgaInit.Initialize()`
  that registers each generated ID type and its `Parse` method into the `IdUtil` runtime
  registries (type ↔ OpenFGA type name, type ↔ parser).

- **`Generators/Fga/SealedFgaSaveChangesInterceptor.Generator.cs` → `SealedFgaSaveChangesInterceptor.g.cs`.**
  An EF Core `SaveChangesInterceptor` subclass with a `ThreadLocal<bool>` recursion guard that
  delegates to the hand-written `SealedFgaSaveChangesProcessor`.

### Base contracts the generated code implements — `AuthModel/`

- `ISealedFgaType<TId>` — marker for entities; exposes `TId Id`.
- `ISealedFgaTypeId<TId>` (+ non-generic `ISealedFgaTypeIdWithoutAssociatedIdType`) — the
  interface generated ID classes implement; also an `ISealedFgaUser`.
- `ISealedFgaUser` — the subject side of a tuple; `AsOpenFgaIdTupleString()`.
- `SealedFgaRelation` (abstract base) + `ISealedFgaRelation<TObjId>` — base for generated
  relation classes; the generic parameter binds a relation to the object-ID type it applies to.
- `SealedFgaUserset<TUserId>` — models an OpenFGA userset subject (`object:id#relation`).
- Converter bases: `GuidIdTypeConverter<>`, `StringIdTypeConverter<>` (`Util/JsonSimpleStringConverter<>`
  is the JSON base).

### Supporting code

- `Util/IdUtil.cs` — runtime registries populated by `SealedFgaInit`; used by the service and
  binders to convert between raw strings and strong IDs (`ParseId`, `GetNameByIdType`).
- `Util/GeneratorUtil.cs`, `Util/RoslynExtensionMethods.cs` — generation helpers.
- `Models/` — generator DTOs: `GeneratedFile` (the universal output unit, prepends an
  auto-generated banner + usings + namespace), `IdClassToGenerateData`, `ModelFgaIncrementalChange`,
  `SealedFgaTypeIdType` (`String` | `Guid`).
- `Attributes/` — `SealedFgaTypeIdAttribute` (drives ID/relation generation),
  `SealedFgaRelationAttribute` (drives the sync; carries `Relation` + `SealedFgaRelationTargetType`),
  `FgaAuthorizeAttribute` / `FgaAuthorizeListAttribute` (drive the model binders).

## Runtime: strongly-typed client — `Fga/SealedFgaService.cs`

A wrapper over `OpenFga.Sdk.Client.OpenFgaClient`. Its typed methods take typed IDs
(`ISealedFgaTypeId<TObjId>`), a typed user (`ISealedFgaUser`), and a typed relation
(`ISealedFgaRelation<TObjId>` — generic over the **same** object-ID type). Because the relation
is bound to the object type, using a relation from the wrong entity type is a compile error and
you can never hand-assemble a malformed tuple string.

Key methods:
- `CheckAsync(user, relation, objectId)` → `bool`.
- `EnsureCheckAsync(user, relation, objectId)` → throws `FgaForbiddenException` when the check
  **fails**.
- `ListObjectsAsync(user, relation)` → `IEnumerable<TObjId>` (maps returned strings back to strong IDs).
- `BatchCheckAsync(checks)` → dictionary keyed by `(user, relation, object)`; currently client-side
  parallel `CheckAsync` calls (the .NET SDK has no native batch check).
- `ModifyIdAsync(oldId, newId)` — rewrites every tuple referencing an entity's old ID to the new ID.
- `DeleteObjectFromOpenFgaIncludingAllRelations(objId)` — removes every tuple where the object
  appears as `Object` or as `User`.
- `SafeWriteTupleAsync` / `SafeDeleteTupleAsync` / `SafeWriteAndDeleteTuplesAsync` — **idempotent**:
  batch-check existence first, only write tuples that don't exist and only delete tuples that do.

Reads are performed directly. Writes/deletes are **not** called from the sync path directly;
the sync path records them into the outbox (see below) and the background drainer applies them
via the idempotent `Safe*` methods / `ModifyIdAsync` / `DeleteAllRelationsForRawObjectAsync`.

## Runtime: secure-by-design model binders — `ModelBinder/`

The base `SealedFgaModelBinder<TAttr>` reads the current OpenFGA subject from the
`open_fga_user` claim on `HttpContext.User`, resolves `SealedFgaService`, then delegates to
`FgaBind`. `SealedFgaModelBinderProvider<TDb>` (registered at index 0 of the MVC model-binder
providers) supplies the concrete `DbContext` type.

- **`SealedFgaEntityModelBinder`** (for `[FgaAuthorize(Relation, ParameterName)]`) —
  **check-then-load**: parse the route ID into a strong ID, `CheckAsync` the relation; on failure
  throw `FgaForbiddenException`; only on success load the entity via `DbContext.Set<T>().FindAsync`
  (throw `FgaEntityNotFoundException` if absent) and inject it. A controller action therefore
  can never receive an entity the caller isn't authorized for.
- **`SealedFgaEntityListModelBinder`** (for `[FgaAuthorizeList(Relation)]`) — **list-then-filter**:
  call `ListObjectsAsync` for the authorized object IDs, then filter the `DbSet` (via a built
  `Where(e => authorizedIds.Contains(e.Id))` expression). The action receives exactly the visible subset.

## Runtime: middleware & exceptions

`Middleware/SealedFgaExceptionHandlerMiddleware` wraps the pipeline and maps:
- `FgaForbiddenException` → **403 Forbidden**
- `FgaEntityNotFoundException` → **404 Not Found**
- anything else → re-thrown.

Both exceptions live in `Exceptions/`. Since the binders throw during model binding and the
middleware sits in front, unauthorized requests never reach the controller.

## Auth-state sync (DB → OpenFGA, unidirectional, transactional outbox)

The sync is a **transactional outbox**. Producing outbox rows happens inside the DB
transaction; applying them to OpenFGA happens afterwards in the background.

Producer side (in the consumer's `SaveChanges` transaction):

1. **`Attributes/SealedFgaRelationAttribute`** — put `[SealedFgaRelation("OwnedBy", targetType)]`
   on an entity property that holds a foreign-key strong ID. `TargetType` (`Object` | `User`)
   decides which side of the tuple the FK sits on.
2. **The generated interceptor** — fires on `SavingChanges` / `SavingChangesAsync` (before
   commit — deliberately, so the rows land in the same transaction), guarded against
   re-entrancy, and calls the processor.
3. **`Fga/SealedFgaSaveChangesProcessor.cs`** — scans `ChangeTracker.Entries()` for entities
   implementing `ISealedFgaType<>` in state `Added | Modified | Deleted` and **appends
   `SealedFgaOutboxEntry` rows** (via `context.Set<SealedFgaOutboxEntry>().AddRange(...)`) — it
   does **not** call OpenFGA:
   - **Added** → `WriteTuple` row.
   - **Modified** → `DeleteTuple` (old) + `WriteTuple` (new); change is detected **by value**
     (`Equals`), not reference. If the primary key changed, a single `ModifyId` row is emitted
     instead (and per-property emission is skipped, to avoid double-handling).
   - **Deleted** → one `DeleteAllForObject` row.
   `ExtractTupleStrings` orients the tuple by `TargetType`: `Object` means the FK entity is the
   tuple's `user` and this entity is the `object`; `User` swaps them. Tuple strings come from
   `ISealedFgaUser.AsOpenFgaIdTupleString()` — no reflection.

Consumer side (background):

4. **`Fga/Outbox/SealedFgaOutboxHostedService<TDbContext>`** — a `BackgroundService` (auto-
   registered by `ConfigureSealedFga<TDbContext>()`) that polls and calls
   **`Fga/Outbox/SealedFgaOutboxDrainer`** in its own DI scope. The drainer applies pending
   rows to OpenFGA in strict `Id` order via the idempotent service methods, marking each
   processed or recording `Attempts`/`NextAttemptUtc` (exponential backoff) / `LastError` on
   failure. Retention/inspection: processed rows stay in the table.

Model registration: **`Fga/Outbox/SealedFgaModelCustomizer`** (an `IModelCustomizer` decorator
wired via `options.ReplaceService<IModelCustomizer, SealedFgaModelCustomizer>()` inside the
generated `AddSealedFga`) adds the `SealedFgaOutboxEntry` entity to the model automatically —
the consumer does not touch `OnModelCreating`. **On relational providers the consumer must add
an EF migration** for the new `SealedFgaOutbox` table.

The direction is strictly **DB → OpenFGA**. The database is the source of truth. Because the
outbox rows commit atomically with the entity changes and OpenFGA is only touched afterward by
the drainer, a rolled-back transaction never leaks tuples to OpenFGA, and transient OpenFGA
outages are retried rather than silently dropped.

### Known limitations / backlog
- **Pagination** — `ListAllRelationsToObjectAsync` / `ListAllRelationsFromUserAsync` read only
  the first OpenFGA `Read` page; large relation sets are truncated during delete/`ModifyId`.
- **Write chunking** — no chunking to OpenFGA's per-transaction write limit (~100 tuples).
- **`BatchCheckAsync`** swallows check errors as `false` (can mask failures) and fans out
  unbounded parallel `Check` calls.
- The recursion guard is `ThreadLocal` (fine while the processor is synchronous).

## DI / wiring API

All of these are **generated** into the consumer's assembly by `SealedFgaExtensions.Generator.cs`:

- `IServiceCollection.ConfigureSealedFga<TDbContext>(Action<SealedFgaOptions>? = null)` — the main
  entry point: registers `SealedFgaService` (scoped), the generated `SealedFgaSaveChangesInterceptor`
  (scoped), inserts `SealedFgaModelBinderProvider<TDbContext>` at index 0, and binds `SealedFgaOptions`.
- `DbContextOptionsBuilder.AddSealedFga(IServiceProvider)` — resolves and adds the SaveChanges interceptor.
- `ModelConfigurationBuilder.ConfigureSealedFga()` — call from `DbContext.ConfigureConventions`;
  registers each generated ID type's `EfCoreValueConverter` so strong IDs persist as their primitive.
- `IApplicationBuilder.UseSealedFga()` — installs `SealedFgaExceptionHandlerMiddleware`.

`SealedFgaOptions` (`SealedFgaOptions.cs`) controls the outbox drainer: `QueueFgaServiceOperations`
(default `true`) gates whether the background drainer runs; `OutboxPollInterval`,
`OutboxBatchSize`, and `OutboxMaxAttempts` tune it. `Settings.cs` holds the namespace constants
the generator uses to build `using` directives.

## Packaging quirks — `SealedFga.csproj`

- Targets **`netstandard2.0`** (required for Roslyn components), `LangVersion=preview`, uses
  `PolySharp` for newer-C# polyfills.
- `IsRoslynComponent=true`, `EnforceExtendedAnalyzerRules=true`, `GeneratePackageOnBuild=true`.
- References `..\openfga-language\pkg\csharp\OpenFga.Language.csproj` with `PrivateAssets="all"`
  ("only required inside the source generator") to parse `model.fga`.
- Because analyzers can't rely on NuGet transitive deps, the built `OpenFga.Language.dll` plus
  the SDK/MVC/Sqlite/Immutable DLLs are explicitly packed into `analyzers/dotnet/cs` via
  `<None ... Pack="true" PackagePath="analyzers/dotnet/cs" Visible="false" />` items.
- Versions come from `../settings.props`.
