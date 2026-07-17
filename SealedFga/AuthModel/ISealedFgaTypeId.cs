namespace SealedFga.AuthModel;

/// <summary>
///     Interface for type IDs for SealedFGA object entities.
/// </summary>
public interface ISealedFgaTypeId<out TId> : ISealedFgaTypeIdWithoutAssociatedIdType, ISealedFgaUser
    where TId : ISealedFgaTypeId<TId>;
