namespace Boys.Ledger.Api.Configuration;

using System.ComponentModel.DataAnnotations;

/// <summary>Typed configuration for the ledger API. Bound from the "Ledger" section and
/// <c>ValidateOnStart</c>-checked, so a missing connection string fails the process at boot —
/// never at the first request. This is the "fail fast" contract B11 tests.</summary>
public sealed class LedgerOptions
{
    public const string SectionName = "Ledger";

    /// <summary>SQL Server (OLTP) connection string. Required — blank fails startup.</summary>
    [Required(AllowEmptyStrings = false, ErrorMessage = "Ledger:SqlConnectionString is required")]
    public string SqlConnectionString { get; init; } = string.Empty;

    /// <summary>brain's gRPC endpoint (quant + referee). Required — blank fails startup.</summary>
    [Required(AllowEmptyStrings = false, ErrorMessage = "Ledger:BrainGrpcAddress is required")]
    public string BrainGrpcAddress { get; init; } = "http://127.0.0.1:50061";

    /// <summary>Per-call deadline for brain gRPC calls, in milliseconds. Past this the call fails
    /// fast into a <c>BrainUnavailableException</c> so a hung brain never hangs the demo.</summary>
    [Range(100, 60000, ErrorMessage = "Ledger:BrainTimeoutMs must be between 100 and 60000")]
    public int BrainTimeoutMs { get; init; } = 3000;

    /// <summary>Directory where submitted proof evidence is stored (referenced by URI). v0: a local
    /// volume path; blank falls back to a temp directory.</summary>
    public string EvidenceDir { get; init; } = string.Empty;

    /// <summary>Demo fund window: a "now" commitment's stake is valued against this historical slice of the
    /// backtested NAV curve (the "simulate the past as if live" mechanic). Defaults to the full curve range.</summary>
    public string FundStartDate { get; init; } = "2021-08-13";

    public string FundAsOfDate { get; init; } = "2024-05-19";
}
