namespace LibPs3Kit;

/// <summary>Why an operation on a PS3 disc image couldn't be done.</summary>
public enum Ps3Error
{
    /// <summary>The image isn't a PS3 disc (no region table, no PS3 filesystem).</summary>
    NotPs3Disc,
    /// <summary>The image is encrypted and no disc key was given.</summary>
    KeyMissing,
    /// <summary>The disc key doesn't decrypt this image (its files don't start as they should).</summary>
    WrongKey,
    /// <summary>A .dkey file or key text couldn't be read.</summary>
    BadKey,
    /// <summary>The ISO 9660 filesystem is damaged or holds something unsafe to extract.</summary>
    BadFilesystem,
    /// <summary>The output already exists and overwriting wasn't allowed.</summary>
    OutputExists,
    /// <summary>The written output didn't read back the same.</summary>
    VerifyFailed,
}

/// <summary>An operation on a PS3 disc image failed for a known reason (<see cref="Error"/>).</summary>
public sealed class Ps3Exception(Ps3Error error, string message, Exception? inner = null) : Exception(message, inner)
{
    public Ps3Error Error { get; } = error;
}
