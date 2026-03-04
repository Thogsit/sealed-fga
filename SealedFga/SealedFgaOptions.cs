namespace SealedFga;

/// <summary>
///     Configuration options for SealedFGA.
/// </summary>
public class SealedFgaOptions {
    /// <summary>
    ///     Currently unused; will determine whether to queue FGA service operations for reliable processing.
    /// </summary>
    public bool QueueFgaServiceOperations { get; set; } = true;
}
