namespace Boys.Ledger.Api.Verification;

using Boys.Ledger.Api.Configuration;
using Microsoft.Extensions.Options;

/// <summary>v0 evidence storage: writes bytes to a local directory and returns a <c>file://</c> URI.
/// The bytes are never logged; only the URI is retained. A real deployment would swap this for object
/// storage behind the same interface.</summary>
public sealed class LocalEvidenceStore : IEvidenceStore
{
    private static readonly IReadOnlyDictionary<string, string> Extensions = new Dictionary<string, string>
    {
        ["image/png"] = ".png",
        ["image/jpeg"] = ".jpg",
        ["application/pdf"] = ".pdf",
    };

    private readonly string _directory;

    public LocalEvidenceStore(IOptions<LedgerOptions> options)
    {
        var configured = options.Value.EvidenceDir;
        _directory = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Path.GetTempPath(), "boys-evidence")
            : configured;
    }

    public async Task<string> StoreAsync(byte[] evidence, string mime, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_directory);
        var extension = Extensions.TryGetValue(mime, out var ext) ? ext : ".bin";
        var fileName = $"{Guid.NewGuid():n}{extension}";
        var path = Path.Combine(_directory, fileName);
        await File.WriteAllBytesAsync(path, evidence, cancellationToken);
        return new Uri(path).AbsoluteUri;
    }
}
