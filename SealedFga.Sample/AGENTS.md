# SealedFga.Sample — Agent Guide

A minimal ASP.NET Core web app (`net10.0`, EF Core **InMemory** provider) that demonstrates and
exercises the SealedFGA framework end-to-end. It references the library **as an analyzer by local
project reference** (not the published NuGet package), so it doubles as the test/debug harness
for changes to `SealedFga/`. For how the framework works internally, see
[`../SealedFga/AGENTS.md`](../SealedFga/AGENTS.md).

## The authorization model — `model.fga`

```
model
  schema 1.1

type user

type agency
  relations
    define Member: [user]

type secret
  relations
    define OwnedBy: [agency]
    define can_view: [user] or Member from OwnedBy
    define can_edit: [user] or Member from OwnedBy
```

- `user` — a bare subject/principal type.
- `agency` — a grouping type; users can be direct `Member`s.
- `secret` — the protected object; `OwnedBy` links it to an agency, and `can_view` / `can_edit`
  are granted either by a direct `[user]` assignment or by being a `Member` of the owning agency.

How relations map to generated C# (see the casing rule in the library guide):
- lowercase relations (`can_view`, `can_edit`) → `SecretEntityIdAttributes.can_view` / `.can_edit`.
- uppercase relations (`OwnedBy`, `Member`) → `SecretEntityIdGroups.OwnedBy`,
  `AgencyEntityIdGroups.Member`.

`model.fga` is fed to the generator via `<AdditionalFiles Include="model.fga" />` in the csproj,
and is also the model bootstrapped into OpenFGA by the `openfga-init` docker step.

## Entities

Each entity's ID is declared as an **empty `partial class`** carrying `[SealedFgaTypeId(name, type)]`;
the generator fills in the body. The entity implements `ISealedFgaType<TId>`.

- **`User/UserEntity.cs`** — `UserEntityId` is `String`-backed. `AgencyId` carries
  `[SealedFgaRelation(nameof(AgencyEntityIdGroups.Member), SealedFgaRelationTargetType.User)]` —
  the user is the tuple's *user* side, i.e. `user:<id>` is `Member` of `agency:<AgencyId>`.
- **`Secret/AgencyEntity.cs`** — `AgencyEntityId` is `Guid`-backed; no relation properties.
- **`Secret/SecretEntity.cs`** — `SecretEntityId` is `Guid`-backed. `OwningAgencyId` carries
  `[SealedFgaRelation(nameof(SecretEntityIdGroups.OwnedBy))]` (default `TargetType = Object`),
  i.e. `agency:<OwningAgencyId>` is `OwnedBy` of `secret:<Id>`.

## Controller patterns — `Secret/SecretController.cs`

The two model-binder patterns and the imperative check:

```csharp
// list-then-filter: receives only the secrets the user may view
[HttpGet]
public IActionResult GetAllSecrets(
    [FgaAuthorizeList(Relation = nameof(SecretEntityIdAttributes.can_view))]
    List<SecretEntity> secrets) => Ok(secrets);

// check-then-load: 403 if not allowed, 404 if missing; else the entity is injected
[HttpGet("{secretId}")]
public IActionResult GetSecretById(
    [FromRoute] SecretEntityId secretId,
    [FgaAuthorize(Relation = nameof(SecretEntityIdAttributes.can_view),
                  ParameterName = nameof(secretId))]
    SecretEntity secret) => Ok(secret);

// imperative check via the service
await sealedFgaService.EnsureCheckAsync(userId, SecretEntityIdAttributes.can_edit, secretId);
```

`PUT /secrets/{secretId}` (with `can_edit`) mutates and `SaveChangesAsync()` — which fires the
sync interceptor. `POST /secrets/{secretId}/toggle-agency` reassigns `OwningAgencyId`, exercising
the modified-relation delete-old + write-new sync path. Relation names are passed via `nameof(...)`
so they're compile-checked.

## App wiring — `Program.cs` & `Database/SealedFgaSampleContext.cs`

- `AddDbContext<SealedFgaSampleContext>` uses `UseInMemoryDatabase(...)` and
  `options.AddSealedFga(sp)` (registers the SaveChanges sync interceptor).
- `builder.Services.ConfigureSealedFga<SealedFgaSampleContext>(opt => opt.QueueFgaServiceOperations = false)`
  registers the service, interceptor, and model-binder provider.
- The `OpenFgaClient` is registered as a singleton, built in three passes against
  `http://localhost:8080`: list stores → take the first store ID → read auth models → take the
  first model ID → return a client configured with both. **This requires the `openfga-init` docker
  step to have created a store + model first.**
- Pipeline order: `UseAuthentication()`, `UseAuthorization()`, `UseRouting()`, `MapControllers()`,
  then `app.UseSealedFga()` (the exception→HTTP middleware).
- `SealedFgaSampleContext.ConfigureConventions` calls `configurationBuilder.ConfigureSealedFga()`
  so the strong ID types persist as their raw `Guid`/`string`. `AddDummyData()` seeds two agencies,
  one user (`user:some-id`, member of agency one), and three secrets.

## Auth — `Auth/MockAuthenticationHandler.cs`

A test authentication handler that always succeeds and injects a single claim
`open_fga_user = "user:some-id"`. This claim type is exactly what the model binders read to
identify the OpenFGA subject — so every request in the sample acts as `user:some-id`.

## Generated output on disk — `Generated/`

Because the csproj sets `EmitCompilerGeneratedFiles=true` and
`CompilerGeneratedFilesOutputPath=Generated` (then `<Compile Remove="Generated/**/*.cs" />` to
avoid double-compilation), the generator output is written to `Generated/SealedFga/...` and is
handy for eyeballing what the generator actually emits (typed IDs, `...Attributes`/`...Groups`
relation classes, `SealedFgaExtensions`, the interceptor, `SealedFgaInit`).

## Running it

```bash
docker compose up -d                        # Postgres + OpenFGA + openfga-init (creates store/model)
dotnet run --project SealedFga.Sample       # app talks to http://localhost:8080
```

Then, as the mock `user:some-id`:
- `GET /secrets` — the filtered list of viewable secrets.
- `GET /secrets/{secretId}` — a single secret (403 if not allowed, 404 if missing).
- `PUT /secrets/{secretId}` — edit a secret (requires `can_edit`); triggers DB→OpenFGA sync.
