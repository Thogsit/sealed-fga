# SealedFGA — Agent Guide

SealedFGA is a framework that integrates [OpenFGA](https://openfga.dev/) fine-grained
authorization into **ASP.NET Core + Entity Framework Core** projects in a strongly-typed,
secure-by-design way. It ships as a single NuGet package (`SealedFga`, currently `v0.0.29`)
that is **both a Roslyn source generator and a runtime library**.

It has two halves that are important to keep in mind when working here:

- A **compile-time half** — a Roslyn `IIncrementalGenerator` (targets `netstandard2.0`) that
  reads your OpenFGA model and generates strongly-typed C# from it.
- A **runtime half** — ordinary library code (the strongly-typed client, model binders,
  middleware, the sync interceptor logic) that the generated code wires up.

The split exists because a Roslyn generator must target `netstandard2.0` and cannot directly
reference the ASP.NET Core / EF Core runtime types it needs to emit against — so the generator
emits thin glue into the consuming project, and the real logic lives in the runtime library.

## The three pillars

1. **Source generation from `model.fga`.** You write your OpenFGA authorization model in a
   `model.fga` file and annotate your entity ID classes. The generator produces strongly-typed
   ID types, strongly-typed relation constants, EF Core value converters, JSON/type converters,
   and the DI/EF wiring — so the OpenFGA type/relation names never appear as bare strings in
   your code.

2. **Strongly-typed client + secure-by-design model binders.** `SealedFgaService` wraps the raw
   `OpenFgaClient` with a typed API where a relation is generic over the object-ID type it
   belongs to, making mismatched or malformed tuples a compile error. ASP.NET Core model binders
   (`[FgaAuthorize]`, `[FgaAuthorizeList]`) enforce the permission check *before* an entity is
   ever handed to a controller action, and an exception→HTTP middleware maps failures to 403/404.

3. **Unidirectional DB→OpenFGA auth-state sync (transactional outbox).** Annotate a
   foreign-key property with `[SealedFgaRelation]` and an EF Core `SaveChanges` interceptor
   records the intended OpenFGA tuple changes into a `SealedFgaOutbox` table **in the same DB
   transaction** as the entity changes; a background drainer then applies them to OpenFGA with
   retries. The database is the source of truth; nothing is written back from OpenFGA into the
   database, and nothing reaches OpenFGA unless the originating transaction committed.

## Repository layout

| Path | What it is | In scope? |
| --- | --- | --- |
| `SealedFga/` | The framework itself (generator + runtime library). See [`SealedFga/AGENTS.md`](SealedFga/AGENTS.md). | Yes |
| `SealedFga.Sample/` | Reference consumer / test app. See [`SealedFga.Sample/AGENTS.md`](SealedFga.Sample/AGENTS.md). | Yes |
| `openfga-language/` | Vendored clone of [openfga/language](https://github.com/openfga/language); the only local change is the added `.NET` DSL parser at `pkg/csharp/`, used by the generator to parse `model.fga`. | No — treat as a working dependency, don't modify |

Notes:
- The root `README.md` lists several benchmark projects (`benchmark/`, `SealedFga.Runtime.Benchmark`,
  `SealedFga.CompileTime.*`). **They are not present in this checkout** — don't expect them.
- The solution file is `SealedFga.slnx` (the newer XML `.slnx` format), containing the three
  projects above.

## Build & run

```bash
# Build everything
dotnet build SealedFga.slnx

# Start local infra: Postgres + OpenFGA (+ openfga-init creates the store & model
# from SealedFga.Sample/model.fga). OpenFGA listens on http://localhost:8080.
docker compose up -d

# Run the sample web app (talks to http://localhost:8080)
dotnet run --project SealedFga.Sample
```

The sample's `OpenFgaClient` discovers the store and authorization-model IDs at startup, so the
`openfga-init` step (which creates them from `model.fga`) must have run first.

## Conventions & config worth knowing

- **Version management**: all package/framework versions are centralized in `settings.props`,
  imported by each `.csproj` via `<Import Project="../settings.props" />`. There is **no**
  `Directory.Build.props` and **no** `global.json`.
- **The sample references the library as an analyzer**, not a normal library reference
  (`OutputItemType="Analyzer"`), and feeds the model in via `<AdditionalFiles Include="model.fga" />`.
  Any file literally named `model.fga` added as `AdditionalFiles` is picked up by the generator.
- The library is packaged as a Roslyn component (`IsRoslynComponent=true`,
  `GeneratePackageOnBuild=true`); its analyzer DLL and dependency DLLs are packed into
  `analyzers/dotnet/cs` inside the nupkg.

## Where to look next

- **[`SealedFga/AGENTS.md`](SealedFga/AGENTS.md)** — the substantive reference: the source-gen
  pipeline (what each generator emits), the strongly-typed client, the model binders, the
  middleware, the sync mechanism, the DI API, and packaging quirks.
- **[`SealedFga.Sample/AGENTS.md`](SealedFga.Sample/AGENTS.md)** — a concrete end-to-end usage
  example: the `model.fga`, the annotated entities, controller patterns, app wiring, and how to
  run it against the docker stack.
