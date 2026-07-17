namespace SealedFga.AuthModel;

/// <summary>
///     Interface for the OpenFGA user object of a relation tuple.
/// </summary>
public interface ISealedFgaUser {
    /// <summary>
    ///     Returns this subject in its OpenFGA tuple string representation.
    /// </summary>
    /// <returns>The OpenFGA subject string.</returns>
    public string AsOpenFgaIdTupleString();
}
