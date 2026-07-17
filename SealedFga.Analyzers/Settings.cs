using System.Collections.Immutable;

namespace SealedFga;

internal static class Settings {
    public const string PackageNamespace = "SealedFga";
    public const string AttributesNamespace = PackageNamespace + ".Attributes";
    public const string AuthModelNamespace = PackageNamespace + ".AuthModel";
    public const string FgaNamespace = PackageNamespace + ".Fga";
    public const string MiddlewareNamespace = PackageNamespace + ".Middleware";
    public const string ModelBinderNamespace = PackageNamespace + ".ModelBinder";
    public const string UtilNamespace = PackageNamespace + ".Util";

    /// <summary>
    ///     Fully qualified metadata name of the runtime library's <c>[SealedFgaTypeId]</c> attribute
    ///     (<c>SealedFga/Attributes/SealedFgaTypeIdAttribute.cs</c>). Referenced by name because the
    ///     generator (netstandard2.0) cannot reference the net10.0 runtime assembly.
    /// </summary>
    public const string SealedFgaTypeIdAttributeMetadataName = AttributesNamespace + ".SealedFgaTypeIdAttribute";
}
