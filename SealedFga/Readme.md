# SealedFGA

A Roslyn source generator + runtime library that gives you **strongly-typed, compile-checked
[OpenFGA](https://openfga.dev/) integration** for ASP.NET Core + EF Core. You write your authorization
model once (`model.fga`) and get generated strong ID types, relation constants, EF value converters,
transactional relationship sync (outbox), and `[FgaAuthorize]` model binders.

For the full source and background, see the [project repository](https://github.com/Thogsit/master-thesis).

## Requirements

- .NET (ASP.NET Core) project with **EF Core 9+**
- A running **OpenFGA** server with a store and your authorization model loaded (see *Manual steps* below)

## Install

```xml
<ItemGroup>
  <FrameworkReference Include="Microsoft.AspNetCore.App" />
  <PackageReference Include="SealedFga" Version="x.y.z" />
  <PackageReference Include="Microsoft.EntityFrameworkCore" Version="9.*" />
</ItemGroup>
```

The package ships MSBuild props that automatically pick up your `*.fga` model file(s) and set
`LangVersion` — **no `<AdditionalFiles>` or `<LangVersion>` edits needed.**

## 1. Author your model — `model.fga`

Add an OpenFGA model file anywhere in your project (it is auto-fed to the generator):

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

> Relation-name casing matters: **lowercase** relations generate `{Entity}IdAttributes.<name>`
> (permission relations); **Uppercase** relations generate `{Entity}IdGroups.<name>` (grouping/parent
> relations).

## 2. Declare strong ID types

One empty `partial class` per OpenFGA object type. The `name` must match a `type` in `model.fga`:

```csharp
[SealedFgaTypeId("widget", SealedFgaTypeIdType.Guid)]
public partial class WidgetEntityId;

[SealedFgaTypeId("owner", SealedFgaTypeIdType.Guid)]
public partial class OwnerEntityId;
```

## 3. Mark your entities

```csharp
public class WidgetEntity : ISealedFgaType<WidgetEntityId> {
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public WidgetEntityId Id { get; set; } = null!;

    // Relation names are generated constants, so they're compile-checked.
    [SealedFgaRelation(nameof(WidgetEntityIdGroups.OwnedBy))]
    public OwnerEntityId OwnerId { get; set; } = null!;
}
```

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
    [FgaAuthorize(Relation = nameof(WidgetEntityIdAttributes.can_view), ParameterName = "id")]
    WidgetEntity widget
) => Ok(widget);
```

Imperative checks are available via `SealedFgaService` / `SealedFgaGuard`.

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
