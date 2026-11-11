namespace SealedFga.Fga;

/// <summary>
///     A check expressed using an OpenFGA type name, relation string, and raw object ID.
///     The generated dispatcher resolves the type and parses the ID using its generated helpers.
/// </summary>
public readonly record struct TypeNameCheck(string TypeName, string Relation, string ObjectId);
