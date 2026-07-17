using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SealedFga.Util;

namespace SealedFga.Models;

internal class IdClassToGenerateData(
    string typeName,
    SealedFgaTypeIdType type,
    string classNamespace,
    string className,
    Location location,
    string underlyingType
) {
    public string TypeName { get; } = typeName;
    public SealedFgaTypeIdType Type { get; } = type;
    public string ClassNamespace { get; } = classNamespace;
    public string ClassName { get; } = className;
    public Location Location { get; } = location;
    public string UnderlyingType { get; } = underlyingType;

    public static IdClassToGenerateData From(
        AttributeData attributeData,
        TypeDeclarationSyntax typeDeclarationSyntax,
        ISymbol typeSymbol
    ) {
        var sealedFgaType = (SealedFgaTypeIdType) attributeData.ConstructorArguments[1].Value!;
        return new IdClassToGenerateData(
            (string) attributeData.ConstructorArguments[0].Value!,
            sealedFgaType,
            typeSymbol.ContainingNamespace.FullName(),
            typeDeclarationSyntax.Identifier.Text,
            attributeData.ApplicationSyntaxReference!.GetSyntax().GetLocation(),
            GeneratorUtil.GetCsharpTypeBySealedFgaIdType(sealedFgaType)
        );
    }
}
