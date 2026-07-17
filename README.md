# SealedFGA

A Roslyn source generator + runtime library that gives you **strongly-typed, compile-checked
[OpenFGA](https://openfga.dev/) integration** for ASP.NET Core + EF Core. You write your
authorization model once (`model.fga`) and get generated strong ID types, relation constants,
EF value converters, transactional relationship sync (outbox), and `[FgaAuthorize]` model binders.

```xml
<ItemGroup>
  <PackageReference Include="SealedFga" Version="x.y.z" />
</ItemGroup>
```

**Full documentation — quickstart, annotations, binders, outbox API — lives in the package
readme: [SealedFga/Readme.md](./SealedFga/Readme.md).**

## Three pillars

1. **Strongly-typed model.** The generator parses your `model.fga` and emits strong ID types
   (`readonly record struct`s with EF/JSON/MVC converters) and compile-checked relation
   constants (`WidgetEntityIdPermissions.can_view`) — a typo'd relation or a tuple linking the
   wrong types is a compile error, not a runtime 403.
2. **Transactional sync.** Entity annotations (`[SealedFgaRelation]`, `[SealedFgaJoinRelation]`)
   and a typed enqueue API write tuple changes into a database outbox inside **your**
   transaction; a leased background drainer applies them to OpenFGA — at-least-once,
   server-side idempotent, newest-wins per tuple key, batched.
3. **Declarative endpoint authorization.** `[FgaAuthorize]` binds a route ID to a loaded,
   authorization-checked entity; `[FgaAuthorizeList]` hands your action an unmaterialized,
   authorization-filtered `IQueryable<T>` that still translates to SQL.

## Requirements

- .NET 10 / ASP.NET Core + EF Core 10 (flow in transitively with the package)
- A running **OpenFGA** server, **minimum version v1.10.0** — SealedFGA applies tuple
  writes/deletes with the Write API's ignore semantics (`on_duplicate: ignore` /
  `on_missing: ignore`), which older servers reject. The compose file and the integration
  tests pin `openfga/openfga:v1.15.1`.

## Repository layout

- [SealedFga](./SealedFga) — the runtime library (published as the `SealedFga` NuGet package,
  with the analyzer/generator payload packed in).
- [SealedFga.Analyzers](./SealedFga.Analyzers) — the Roslyn source generator (netstandard2.0;
  ships inside the `SealedFga` package, not as a separate package).
- [SealedFga.Sample](./SealedFga.Sample) — a sample ASP.NET Core app using SealedFGA by project
  reference; also the app the integration tests run against.
- [openfga-language](./openfga-language) — vendored clone of
  [openfga/language](https://github.com/openfga/language) (Apache-2.0); the only change is the
  added .NET parser project (`pkg/csharp`) that SealedFGA's generator uses to transform the FGA
  DSL. Its output is ILRepack-merged into the analyzer payload.
- [SealedFga.Tests](./SealedFga.Tests), [SealedFga.IntegrationTests](./SealedFga.IntegrationTests),
  [SealedFga.PackagingTests](./SealedFga.PackagingTests) — see below.

A `docker-compose.yml` is included to run PostgreSQL and OpenFGA for local development.

## Building and testing

```bash
dotnet build SealedFga.slnx
```

Three test suites:

```bash
# 1. Unit + generator snapshot tests
dotnet test SealedFga.Tests

# 2. Integration tests — needs a running Docker daemon (Testcontainers spins up
#    ephemeral OpenFGA and PostgreSQL containers)
dotnet test SealedFga.IntegrationTests

# 3. Packaging tests — consume SealedFga as the packed NuGet package
#    (not part of the solution; see SealedFga.PackagingTests/README.md)
dotnet pack SealedFga/SealedFga.csproj -c Debug -o SealedFga.PackagingTests/local-packages
dotnet nuget locals global-packages --clear   # or: rm -rf ~/.nuget/packages/sealedfga
dotnet test SealedFga.PackagingTests/SealedFga.PackagingTests.csproj -c Debug
```

## License

MIT — see [LICENSE](./LICENSE). The vendored [openfga-language](./openfga-language) directory
is Apache-2.0 (see its [LICENSE](./openfga-language/LICENSE) and
[THIRD-PARTY-NOTICES.md](./THIRD-PARTY-NOTICES.md)).
