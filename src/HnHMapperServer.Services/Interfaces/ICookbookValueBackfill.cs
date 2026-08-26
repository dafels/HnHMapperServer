namespace HnHMapperServer.Services.Interfaces;

/// <summary>
/// One-time repair of every tenant's canonical food values (see CookbookValueBackfill).
/// Idempotent: a marker records that it has run, so later starts are a no-op.
/// </summary>
public interface ICookbookValueBackfill
{
    Task<CookbookValueBackfillResult> RunOnceAsync(CancellationToken ct = default);
}

/// <summary>Outcome of the one-time cookbook value repair.</summary>
public class CookbookValueBackfillResult
{
    /// <summary>True when the repair had already run and nothing was done.</summary>
    public bool AlreadyApplied { get; set; }

    public int TenantsProcessed { get; set; }

    /// <summary>Tenants whose repair threw; the marker is withheld so the pass repeats.</summary>
    public int TenantsFailed { get; set; }

    public int Foods { get; set; }

    public int Updated { get; set; }

    /// <summary>True when the marker was stored, i.e. this pass will not run again.</summary>
    public bool MarkerWritten { get; set; }
}
