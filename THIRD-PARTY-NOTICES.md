# Third-party notices

SealedFGA is licensed under the MIT License (see [LICENSE](./LICENSE)). It incorporates the
following third-party software:

## openfga/language

- Source: https://github.com/openfga/language
- License: Apache License 2.0 (see [openfga-language/LICENSE](./openfga-language/LICENSE))
- Copyright: The OpenFGA authors

The [`openfga-language/`](./openfga-language) directory is a vendored clone of the upstream
repository with one addition: a .NET parser project (`pkg/csharp/OpenFga.Language.csproj`) that
transforms the OpenFGA DSL to its JSON representation. The resulting `OpenFga.Language.dll`
(with its ANTLR runtime dependency ILRepack-merged in) is distributed inside the `SealedFga`
NuGet package under `analyzers/dotnet/cs`, under the terms of the Apache License 2.0.
