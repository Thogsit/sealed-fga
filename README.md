# SealedFGA

Contains the .NET OpenFGA framework, related projects and the benchmark scripts.

- [SealedFga](./SealedFga): The SealedFGA framework's source code.
- [SealedFga.Sample](./SealedFga.Sample): Contains a sample project that uses SealedFGA by local reference instead of the NuGet package. Used for testing and debugging.
- [OpenFga.Language](./openfga-language): Contains the OpenFGA language project cloned from [here](https://github.com/openfga/language). The only change is the newly added .NET parser project used by SealedFGA.
- [Benchmark Scripts](./benchmark): Contains the scripts used to run the benchmarks.
- [SealedFga.Runtime.Benchmark](./SealedFga.Runtime.Benchmark): Contains the runtime benchmark project.
- [SealedFga.CompileTime.Small.OpenFga](./SealedFga.CompileTime.Small.OpenFga): Contains the compile-time benchmark small sized project in the OpenFGA variant.
- [SealedFga.CompileTime.Small.SealedFga](./SealedFga.CompileTime.Medium.OpenFga): Contains the compile-time benchmark small sized project in the SealedFGA variant.
- [SealedFga.CompileTime.Medium.OpenFga](./SealedFga.CompileTime.Medium.OpenFga): Contains the compile-time benchmark medium sized project in the OpenFGA variant.
- [SealedFga.CompileTime.Medium.SealedFga](./SealedFga.CompileTime.Medium.SealedFga): Contains the compile-time benchmark medium sized project in the SealedFGA variant.

Also, a `docker-compose.yml` file is included which can be used to run the PostgreSQL database and an OpenFGA service for local development.
