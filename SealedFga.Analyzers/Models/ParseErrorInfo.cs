namespace SealedFga.Models;

/// <summary>
///     A value-equatable projection of an OpenFGA DSL parse error, carried through the incremental
///     generator pipeline (the parser's own error type holds ANTLR state and is not equatable).
///     All coordinates are 0-based, matching Roslyn's <c>LinePosition</c>.
/// </summary>
internal readonly record struct ParseErrorInfo(string Message, int Line, int ColumnStart, int ColumnEnd);
