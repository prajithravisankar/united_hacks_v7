namespace Boys.Ledger.Api.Verification;

/// <summary>Stores proof evidence bytes and returns a stable URI reference. The URI (never the bytes)
/// is what the verification row records and what logs may mention.</summary>
public interface IEvidenceStore
{
    Task<string> StoreAsync(byte[] evidence, string mime, CancellationToken cancellationToken = default);
}
