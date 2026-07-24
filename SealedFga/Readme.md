# SealedFGA

A Roslyn source generator + runtime library that gives you **strongly-typed, compile-checked
[OpenFGA](https://openfga.dev/) integration** for ASP.NET Core + EF Core. You write your authorization
model once (`model.fga`) and get generated strong ID types, relation constants, EF value converters,
transactional relationship sync (outbox), and `[FgaAuthorize]` model binders.

For the full source and background, see the [project repository](https://github.com/Thogsit/sealed-fga).

## Requirements

- An ASP.NET Core project targeting **.NET 10** (EF Core 10 and the ASP.NET Core framework
  reference flow in transitively with the package)
- A running **OpenFGA** server with a store and your authorization model loaded (see *Manual steps* below)

## Install

```xml
<ItemGroup>
  <PackageReference Include="SealedFga" Version="x.y.z" />
</ItemGroup>
```

One reference is all it takes: the package brings EF Core 10, OpenFga.Sdk, and the
`Microsoft.AspNetCore.App` framework reference with it (you still add your EF Core *provider*,
e.g. Npgsql). It also ships MSBuild props that automatically pick up your `*.fga` model file(s)
and set `LangVersion` — **no `<AdditionalFiles>` or `<LangVersion>` edits needed.**

## 1. Author your model — `model.fga`

Add an OpenFGA model file anywhere in your project (it is auto-fed to the generator; any
`*.fga` file name works, but only one per project):

```
model
  schema 1.1

type user
type owner
  relations
    define Member: [user]
type widget
  relations
    define OwnedBy: [owner]
    define can_view: [user] or Member from OwnedBy
```

> Relation-name casing matters: **lowercase** relations generate `{Entity}IdPermissions.<name>`
> (permission relations); **Uppercase** relations generate `{Entity}IdGroups.<name>` (grouping/parent
> relations). The split is purely organizational — to disable it, set the MSBuild property
> `<SealedFgaSplitRelationClasses>false</SealedFgaSplitRelationClasses>` in your csproj and all
> relations land in a single `{Entity}IdRelations` class instead.

## 2. Declare strong ID types

One empty `readonly partial record struct` per OpenFGA object type. The `name` must match a
`type` in `model.fga`:

```csharp
[SealedFgaTypeId("widget", SealedFgaTypeIdType.Guid)]
public readonly partial record struct WidgetEntityId;

[SealedFgaTypeId("owner", SealedFgaTypeIdType.Guid)]
public readonly partial record struct OwnerEntityId;
```

IDs are immutable value types with value equality. Mind that `default(WidgetEntityId)` is
representable (all-zero `Guid` / `null` string) and never denotes an existing entity — don't let
uninitialized IDs flow into tuples or queries.

## 3. Mark your entities

Three declaration shapes are supported: two attribute annotations for tuples that mirror FK
values, and `ISealedFgaTupleSource` for entities whose whole tuple set is derivable from the row.
Everything else (cross-row computations, bulk fan-outs) goes through the typed enqueue API.

**Scalar FK** — `[SealedFgaRelation]` on a strong-ID FK property of an `ISealedFgaType<TId>`
entity. The tuple links the FK value and the entity's own `Id`; `TargetType` orients it
(default `Object`: FK is the tuple's `user`, this entity the `object`; `User` swaps them):

```csharp
public class WidgetEntity : ISealedFgaType<WidgetEntityId> {
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public WidgetEntityId Id { get; set; }

    // Relation names are generated constants, so they're compile-checked.
    // Declare the FK as OwnerEntityId? if the relation is optional (null -> no tuple).
    [SealedFgaRelation(nameof(WidgetEntityIdGroups.OwnedBy))]
    public OwnerEntityId OwnerId { get; set; }
}
// add:    widget gets owner:<OwnerId> OwnedBy widget:<Id>
// re-point FK: delete old tuple + write new
// delete: purge every tuple referencing widget:<Id>
```

**Join entity** — class-level `[SealedFgaJoinRelation]` on a many-to-many row. The tuple links
the two named FK properties; the row's own PK appears on neither side, so the entity needs **no**
`ISealedFgaType<TId>` and no OpenFGA type of its own:

```csharp
[SealedFgaJoinRelation(
    nameof(WidgetEntityIdPermissions.can_view),
    userProperty: nameof(UserId),
    objectProperty: nameof(WidgetId))]
public class WidgetShareEntity {
    public Guid Id { get; set; }
    public UserEntityId? UserId { get; set; }
    public WidgetEntityId? WidgetId { get; set; }
}
// add:    user:<UserId> can_view widget:<WidgetId>   (skipped while either FK is null)
// re-point either FK: delete old pair's tuple + write new pair's
// delete: delete exactly that tuple (no purge — nothing references the row's own PK)
```

**Tuple source** — implement `ISealedFgaTupleSource` when the entity's tuples are a **pure
function of its row values** and the attribute shapes can't express them: state machines whose
rows are never hard-deleted, permission sets stored on the row, tuples carrying the row's own id
on the *user* side. `DesiredTuples()` returns the complete tuple set for the current values; the
SaveChanges interceptor diffs it across every tracked change and enqueues exactly the difference:

```csharp
public class ShareGrantEntity : ISealedFgaType<ShareGrantEntityId>, ISealedFgaTupleSource {
    public ShareGrantEntityId Id { get; set; }
    public GrantState State { get; set; }
    public UserEntityId RecipientId { get; set; }
    public WidgetEntityId WidgetId { get; set; }
    public bool CanEdit { get; set; }

    public IEnumerable<SealedFgaTupleOperation> DesiredTuples() {
        if (State != GrantState.Active) yield break;   // inactive states: no tuples, row stays

        // The grant itself on the tuple's USER side — both orientations are supported.
        yield return SealedFgaTupleOperation.Of(Id, WidgetEntityIdGroups.ShareGrant, WidgetId);
        yield return SealedFgaTupleOperation.Of(RecipientId, WidgetEntityIdPermissions.can_view, WidgetId);
        if (CanEdit)
            yield return SealedFgaTupleOperation.Of(RecipientId, WidgetEntityIdPermissions.can_edit, WidgetId);
    }
}
// add:      write DesiredTuples(new row)
// modify:   diff DesiredTuples(original values) vs DesiredTuples(current) -> write/delete the difference
// delete:   delete DesiredTuples(original values) — NO purge fence (see below)
```

Rules and semantics:

- **Purity.** `DesiredTuples()` must depend only on mapped, non-navigation properties — no DB or
  service access (static helpers like a permission catalog are fine). The interceptor evaluates
  the *original* row via a detached instance materialized from EF's original values; impure
  implementations would diff garbage.
- **The tuple source owns all of the entity's tuples.** Combining it with `[SealedFgaRelation]` /
  `[SealedFgaJoinRelation]` on the same entity is rejected (compile-time `SFGA004`, plus a
  runtime check) — express those tuples in `DesiredTuples()` instead.
- **No delete purge.** Unlike attribute-annotated `ISealedFgaType` entities, deleting a
  tuple-source entity enqueues targeted deletes from the diff, not a `DeleteAllForObject` fence:
  the diff is exhaustive by construction, and desired tuples may reference the row's id on
  neither side (permission fan-outs), where an id-keyed purge could never reach.
- ⚠️ **Change-tracker bypasses don't sync.** `ExecuteUpdate` / `ExecuteDelete` / raw SQL never
  hit the interceptor, so tuple-source rows mutated that way silently diverge from OpenFGA.
  Mutate them only through tracked entities — and if bypasses (or manual DB edits) can happen in
  your system, run a periodic reconciliation sweep as a backstop.

In your `DbContext`, register the generated strong-ID value converters (one line — this must run in the
pre-convention phase, so it goes in `ConfigureConventions`):

```csharp
protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    => configurationBuilder.ConfigureSealedFga();
```

## 4. Wire it up — `Program.cs`

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<MyContext>((sp, o) => {
    o.UseNpgsql(connectionString);   // or any provider
    o.AddSealedFga(sp);              // interceptor + strong-ID converters + outbox entity
});

// Binds the "SealedFga" config section and registers the OpenFgaClient, services, and model binders.
builder.Services.ConfigureSealedFga<MyContext>(builder.Configuration);

// ... your authentication (must emit the user claim, see below) + AddAuthorization() ...

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.UseRouting();
app.MapControllers();
app.UseSealedFga();   // maps FgaForbiddenException -> 403, FgaEntityNotFoundException -> 404
```

That's it — **no `SealedFgaInit.Initialize()` call**: init runs automatically via a generated module
initializer, and the outbox entity is registered automatically by `AddSealedFga`.

### Configuration (`appsettings.json`)

```json
{
  "SealedFga": {
    "ApiUrl": "http://localhost:8080",
    "StoreId": "01H...",
    "AuthorizationModelId": "01H...",
    "UserClaimType": "open_fga_user"
  }
}
```

All options can also be set via the `configure` action:
`ConfigureSealedFga<MyContext>(builder.Configuration, o => o.QueueFgaServiceOperations = false)`.
If you need a custom `OpenFgaClient` (e.g. credentials/HTTP handlers), register your own
`OpenFgaClient` singleton **before** `ConfigureSealedFga` — it wins over the built-in one.

## 5. Authorize actions

```csharp
[HttpGet("{id}")]
public IActionResult Get(
    [FgaAuthorize(Relation = nameof(WidgetEntityIdPermissions.can_view), ParameterName = "id")]
    WidgetEntity widget
) => Ok(widget);
```

List endpoints receive an **unmaterialized, authorization-filtered `IQueryable<T>`** — compose
paging/sorting onto it and materialize in the action (everything still translates to SQL):

```csharp
[HttpGet]
public async Task<IActionResult> GetAll(
    [FgaAuthorizeList(Relation = nameof(WidgetEntityIdPermissions.can_view))]
    IQueryable<WidgetEntity> widgets,
    [FromQuery] int page = 0, [FromQuery] int pageSize = 20
) => Ok(await widgets.OrderBy(w => w.Name).Skip(page * pageSize).Take(pageSize).ToListAsync());
```

Imperative checks are available via `SealedFgaService` (e.g. `EnsureCheckAsync`); all check/list
methods take an optional `SealedFgaQueryOptions` (contextual tuples via
`SealedFgaContextualTuple.Of(user, relation, objectId)`, consistency via the SDK's
`ConsistencyPreference`). To apply such options on every **binder-driven** check/list (e.g. a
super-user contextual tuple derived from the request), register an
`ISealedFgaBinderOptionsProvider` in DI — not registering one keeps the default behavior.

## 6. Enqueue computed tuple changes (typed outbox API)

For tuple changes neither an annotation nor a per-row tuple source can express — cross-row
computations, cascades, bulk fan-outs — enqueue precomputed diffs directly into the same
transactional outbox:

```csharp
// Single operations
db.EnqueueFgaWrite(userId, WidgetEntityIdPermissions.can_view, widgetId);
db.EnqueueFgaDelete(userId, WidgetEntityIdPermissions.can_view, widgetId);

// Userset subjects (owner:X#Member)
db.EnqueueFgaWrite(
    SealedFgaUserset<OwnerEntityId>.From(ownerId, OwnerEntityIdGroups.Member),
    WidgetEntityIdPermissions.can_view, widgetId);

// Batches, for 1,000+-row fan-outs
db.EnqueueFga(
    writes: pairs.Select(p => SealedFgaTupleOperation.Of(p.User, WidgetEntityIdPermissions.can_view, p.Widget)),
    deletes: []);
```

Enqueue is synchronous and local: it only adds outbox rows to the change tracker, so the
operations commit **iff your transaction commits** — same guarantee as the annotation-driven
sync, applied by the same background drainer (at-least-once, server-side idempotent,
newest-wins per tuple key). There is deliberately **no raw-string entry point**; relations are
compile-bound to their object type, so a malformed tuple cannot be assembled.

Outbox visibility: `db.GetSealedFgaOutboxStatsAsync()` returns pending/parked counts and
oldest-pending age, and `SealedFgaOutboxHealthCheck<TDbContext>` is an opt-in
`IHealthCheck` (`builder.Services.AddHealthChecks().AddCheck<...>`).

## Manual steps (by design)

These remain your responsibility — SealedFGA does not provision infrastructure for you:

1. **Run OpenFGA, create a store, and push your model.** Use the same `model.fga` you feed the generator
   (e.g. `fga store create --name my-app --model model.fga`). Put the resulting store/model ids in the
   `SealedFga` config section.
2. **Emit the user claim.** Your authentication must add a claim of type `UserClaimType`
   (default `open_fga_user`) whose value is the OpenFGA subject, e.g. `user:<id>`.
3. **Add an EF migration for the outbox** (`SealedFgaOutbox` table) on relational providers — the
   `SealedFgaOutboxEntry` entity is added to your model automatically, but the table still needs a
   migration.
