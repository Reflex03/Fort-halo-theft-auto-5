namespace CthExtract.Gta.Crypto;

/// <summary>Thrown when an archive needs key material we do not have; callers report the row Missing with the message.</summary>
public sealed class KeyUnavailableException(string message) : Exception(message);

/// <summary>Keys available for this run. Either part may be missing; archives that need it then fail cleanly.</summary>
public sealed class GtaKeys
{
    public byte[]? Aes { get; init; }
    public NgKeySet? Ng { get; init; }
    /// <summary>Why AES / NG are unavailable (for the per-row Missing reason).</summary>
    public string? AesProblem { get; init; }
    public string? NgProblem { get; init; }

    public static readonly GtaKeys None = new() { AesProblem = "no AES key", NgProblem = "no NG keys" };

    public byte[] RequireAes(string what) =>
        Aes ?? throw new KeyUnavailableException($"{what} is AES-encrypted and the AES key is unavailable ({AesProblem ?? "unknown"})");

    public NgKeySet RequireNg(string what) =>
        Ng ?? throw new KeyUnavailableException($"{what} is NG-encrypted and the NG keys are unavailable ({NgProblem ?? "unknown"})");
}
