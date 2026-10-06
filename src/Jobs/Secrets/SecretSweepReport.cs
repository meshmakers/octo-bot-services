using Meshmakers.Octo.Runtime.Contracts.Secrets;

namespace Meshmakers.Octo.Backend.Jobs.Secrets;

/// <summary>
///     What started a secret sweep.
/// </summary>
public enum SecretSweepTrigger
{
    /// <summary>
    ///     Started on demand (API, CLI, dashboard) for one tenant or all tenants.
    /// </summary>
    Manual = 0,

    /// <summary>
    ///     The recurring verify job.
    /// </summary>
    Recurring = 1,

    /// <summary>
    ///     A repository restore (concept §6, decision 5).
    /// </summary>
    Restore = 2
}

/// <summary>
///     Outcome of the secret sweep of one tenant.
/// </summary>
public enum SecretSweepOutcome
{
    /// <summary>
    ///     Every step completed without failures.
    /// </summary>
    Succeeded = 0,

    /// <summary>
    ///     Every step completed, but some values could not be processed (see the steps' failures).
    /// </summary>
    CompletedWithFailures = 1,

    /// <summary>
    ///     Nothing was done (see <see cref="SecretSweepReport.Reason" />), e.g. no keys configured or the
    ///     pre-sweep dump could not be taken.
    /// </summary>
    Skipped = 2,

    /// <summary>
    ///     The sweep aborted with an error (see <see cref="SecretSweepReport.Reason" />).
    /// </summary>
    Failed = 3
}

/// <summary>
///     Report of the secret sweep of one tenant (AB#5539, concept §5.3: "per-tenant sweep report in the job
///     result"). Carries counts, CK type names, runtime ids and attribute paths - never a value, never
///     ciphertext.
/// </summary>
public class SecretSweepReport
{
    /// <summary>
    ///     Tenant.
    /// </summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    ///     The requested mode. Writing modes are followed by a <see cref="SecretSweepMode.Verify" /> step that
    ///     describes the state after the sweep; a restore runs <c>Verify</c>, <c>Encrypt</c>, <c>Verify</c> and
    ///     never deletes anything (decisions 2026-10-06, item 2).
    /// </summary>
    public SecretSweepMode Mode { get; set; }

    /// <summary>
    ///     What started the sweep.
    /// </summary>
    public SecretSweepTrigger Trigger { get; set; }

    /// <summary>
    ///     Outcome.
    /// </summary>
    public SecretSweepOutcome Outcome { get; set; }

    /// <summary>
    ///     Why the sweep was skipped or failed, or a remark (e.g. "pre-sweep backup not required"). Never
    ///     contains a value.
    /// </summary>
    public string? Reason { get; set; }

    /// <summary>
    ///     Start (UTC).
    /// </summary>
    public DateTime StartedAt { get; set; }

    /// <summary>
    ///     End (UTC).
    /// </summary>
    public DateTime CompletedAt { get; set; }

    /// <summary>
    ///     File name of the pre-sweep dump in the secret backup directory (<c>Bot:SecretSweep:BackupStoragePath</c>,
    ///     per tenant subdirectory), or <c>null</c> when none was taken.
    /// </summary>
    public string? BackupFileName { get; set; }

    /// <summary>
    ///     Active key id of the key ring at the time of the sweep, or <c>null</c> when no key is configured.
    /// </summary>
    public string? ActiveKeyId { get; set; }

    /// <summary>
    ///     True when strict mode was in force for this environment (<c>Bot:SecretSweep:StrictModeSince</c>).
    /// </summary>
    public bool StrictModeActive { get; set; }

    /// <summary>
    ///     True when strict mode was in force and the final state still holds legacy values (clear text or
    ///     <c>enc:v1</c>) - the alert condition of concept §5.3.
    /// </summary>
    public bool StrictModeViolation { get; set; }

    /// <summary>
    ///     Clear-text plus <c>enc:v1</c> values in the final state (the last step), i.e. what still has to be
    ///     encrypted.
    /// </summary>
    public long RemainingLegacyValues { get; set; }

    /// <summary>
    ///     The steps in execution order.
    /// </summary>
    public List<SecretSweepStepReport> Steps { get; set; } = [];

    /// <summary>
    ///     The secrets that must be re-entered: values stored with a key id unknown to this environment
    ///     (typically after a cross-environment or child-tenant restore; kept encrypted, see
    ///     <see cref="Unreadable" />) and, after <see cref="SecretSweepMode.CleanupUnreadable" />, the values it
    ///     deleted.
    /// </summary>
    public List<SecretValueReference> SecretsToReEnter { get; set; } = [];

    /// <summary>
    ///     Legacy clear-text placeholders converted once to "not set" over all steps (migration only).
    /// </summary>
    public long PlaceholdersNormalized { get; set; }

    /// <summary>
    ///     Stored values whose key id is not in this environment's key ring, in the final state (the last
    ///     step): kept encrypted, read as "key missing", readable again once the key is added to the ring -
    ///     the re-entry list (decisions 2026-10-06, item 2).
    /// </summary>
    public List<SecretUnreadableValueReport> Unreadable { get; set; } = [];
}

/// <summary>
///     One sweep step (one call of <c>ISecretMaintenanceService.SweepTenantAsync</c>).
/// </summary>
public class SecretSweepStepReport
{
    /// <summary>
    ///     Mode of the step.
    /// </summary>
    public SecretSweepMode Mode { get; set; }

    /// <summary>
    ///     Start (UTC).
    /// </summary>
    public DateTime StartedAt { get; set; }

    /// <summary>
    ///     End (UTC).
    /// </summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    ///     CK types scanned.
    /// </summary>
    public int CkTypesScanned { get; set; }

    /// <summary>
    ///     Entities read.
    /// </summary>
    public long EntitiesScanned { get; set; }

    /// <summary>
    ///     Entities with at least one rewritten attribute.
    /// </summary>
    public long EntitiesRewritten { get; set; }

    /// <summary>
    ///     Values changed.
    /// </summary>
    public long ValuesRewritten { get; set; }

    /// <summary>
    ///     Placeholders normalised to "not set".
    /// </summary>
    public long PlaceholdersNormalized { get; set; }

    /// <summary>
    ///     Attributes left alone because they changed while the sweep ran.
    /// </summary>
    public long SkippedConcurrentlyModified { get; set; }

    /// <summary>
    ///     True when nothing failed.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    ///     Counts per form over the tenant, as found by this step (before it acted).
    /// </summary>
    public SecretFormCountsReport Totals { get; set; } = new();

    /// <summary>
    ///     Counts per CK type and Secret slot.
    /// </summary>
    public List<SecretSlotCountsReport> Slots { get; set; } = [];

    /// <summary>
    ///     Values deleted because their key id is unknown - only filled by
    ///     <see cref="SecretSweepMode.CleanupUnreadable" />.
    /// </summary>
    public List<SecretValueReference> Cleared { get; set; } = [];

    /// <summary>
    ///     Values kept although their key id is unknown (every mode except
    ///     <see cref="SecretSweepMode.CleanupUnreadable" />).
    /// </summary>
    public List<SecretUnreadableValueReport> Unreadable { get; set; } = [];

    /// <summary>
    ///     Values that could not be processed.
    /// </summary>
    public List<SecretSweepFailureReport> Failures { get; set; } = [];
}

/// <summary>
///     Counts of Secret values per stored form.
/// </summary>
public class SecretFormCountsReport
{
    /// <summary>
    ///     No value.
    /// </summary>
    public long NotSet { get; set; }

    /// <summary>
    ///     Legacy placeholder or empty string.
    /// </summary>
    public long Placeholder { get; set; }

    /// <summary>
    ///     Legacy clear text.
    /// </summary>
    public long Plaintext { get; set; }

    /// <summary>
    ///     Legacy <c>enc:v1</c>.
    /// </summary>
    public long EncV1 { get; set; }

    /// <summary>
    ///     <c>enc:v2</c> with a known key id.
    /// </summary>
    public long EncV2 { get; set; }

    /// <summary>
    ///     <c>enc:v2</c> with a known key id, per key id.
    /// </summary>
    public Dictionary<string, long> EncV2ByKeyId { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     <c>enc:v2</c> with an unknown key id.
    /// </summary>
    public long UnknownKeyId { get; set; }

    /// <summary>
    ///     <c>enc:v2</c> with an unknown key id, per key id.
    /// </summary>
    public Dictionary<string, long> UnknownKeyIdByKeyId { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Values that could not be processed (counted in their form as well).
    /// </summary>
    public long Failed { get; set; }

    /// <summary>
    ///     All classified slots.
    /// </summary>
    public long Total { get; set; }

    /// <summary>
    ///     Clear text plus <c>enc:v1</c>.
    /// </summary>
    public long Legacy => Plaintext + EncV1;

    internal static SecretFormCountsReport From(SecretFormCounts counts)
    {
        return new SecretFormCountsReport
        {
            NotSet = counts.NotSet,
            Placeholder = counts.Placeholder,
            Plaintext = counts.Plaintext,
            EncV1 = counts.EncV1,
            EncV2 = counts.EncV2,
            EncV2ByKeyId = new Dictionary<string, long>(counts.EncV2ByKeyId, StringComparer.OrdinalIgnoreCase),
            UnknownKeyId = counts.UnknownKeyId,
            UnknownKeyIdByKeyId =
                new Dictionary<string, long>(counts.UnknownKeyIdByKeyId, StringComparer.OrdinalIgnoreCase),
            Failed = counts.Failed,
            Total = counts.Total
        };
    }
}

/// <summary>
///     Counts for one Secret slot of one CK type.
/// </summary>
public class SecretSlotCountsReport
{
    /// <summary>
    ///     CK type.
    /// </summary>
    public string CkTypeId { get; set; } = string.Empty;

    /// <summary>
    ///     Attribute name or record path.
    /// </summary>
    public string AttributePath { get; set; } = string.Empty;

    /// <summary>
    ///     Counts per form.
    /// </summary>
    public SecretFormCountsReport Counts { get; set; } = new();
}

/// <summary>
///     Reference to one Secret value (never the value itself).
/// </summary>
public class SecretValueReference
{
    /// <summary>
    ///     CK type of the entity.
    /// </summary>
    public string CkTypeId { get; set; } = string.Empty;

    /// <summary>
    ///     Runtime id of the entity.
    /// </summary>
    public string RtId { get; set; } = string.Empty;

    /// <summary>
    ///     Attribute name or record path (record elements by record key).
    /// </summary>
    public string AttributePath { get; set; } = string.Empty;

    /// <summary>
    ///     Form the value had.
    /// </summary>
    public SecretValueForm PreviousForm { get; set; }

    /// <summary>
    ///     Key id of the envelope, if any.
    /// </summary>
    public string? KeyId { get; set; }

    internal static SecretValueReference From(SecretSweepClearedValue cleared)
    {
        return new SecretValueReference
        {
            CkTypeId = cleared.CkTypeId,
            RtId = cleared.RtId.ToString(),
            AttributePath = cleared.AttributePath,
            PreviousForm = cleared.PreviousForm,
            KeyId = cleared.KeyId
        };
    }

    internal static SecretValueReference From(SecretUnreadableValueReport unreadable)
    {
        return new SecretValueReference
        {
            CkTypeId = unreadable.CkTypeId,
            RtId = unreadable.RtId,
            AttributePath = unreadable.AttributePath,
            PreviousForm = SecretValueForm.UnknownKeyId,
            KeyId = unreadable.KeyId
        };
    }
}

/// <summary>
///     A stored Secret value that cannot be read because its key id is not in the key ring (never the value
///     itself) - an entry of the re-entry list.
/// </summary>
public class SecretUnreadableValueReport
{
    /// <summary>
    ///     CK type of the entity.
    /// </summary>
    public string CkTypeId { get; set; } = string.Empty;

    /// <summary>
    ///     Runtime id of the entity.
    /// </summary>
    public string RtId { get; set; } = string.Empty;

    /// <summary>
    ///     Attribute name or record path (record elements by record key).
    /// </summary>
    public string AttributePath { get; set; } = string.Empty;

    /// <summary>
    ///     Key id of the envelope.
    /// </summary>
    public string? KeyId { get; set; }

    internal static SecretUnreadableValueReport From(SecretSweepUnreadableValue unreadable)
    {
        return new SecretUnreadableValueReport
        {
            CkTypeId = unreadable.CkTypeId,
            RtId = unreadable.RtId.ToString(),
            AttributePath = unreadable.AttributePath,
            KeyId = unreadable.KeyId
        };
    }
}

/// <summary>
///     A value the sweep could not process.
/// </summary>
public class SecretSweepFailureReport
{
    /// <summary>
    ///     CK type of the entity.
    /// </summary>
    public string CkTypeId { get; set; } = string.Empty;

    /// <summary>
    ///     Runtime id of the entity.
    /// </summary>
    public string RtId { get; set; } = string.Empty;

    /// <summary>
    ///     Attribute name or record path.
    /// </summary>
    public string AttributePath { get; set; } = string.Empty;

    /// <summary>
    ///     Value-free reason.
    /// </summary>
    public string Reason { get; set; } = string.Empty;
}

/// <summary>
///     Result of a secret sweep over all tenants of the instance.
/// </summary>
public class SecretSweepRunSummary
{
    /// <summary>
    ///     Requested mode.
    /// </summary>
    public SecretSweepMode Mode { get; set; }

    /// <summary>
    ///     What started the run.
    /// </summary>
    public SecretSweepTrigger Trigger { get; set; }

    /// <summary>
    ///     Start (UTC).
    /// </summary>
    public DateTime StartedAt { get; set; }

    /// <summary>
    ///     End (UTC).
    /// </summary>
    public DateTime CompletedAt { get; set; }

    /// <summary>
    ///     One report per tenant (system tenant first).
    /// </summary>
    public List<SecretSweepReport> Tenants { get; set; } = [];

    /// <summary>
    ///     Number of tenants per outcome.
    /// </summary>
    public int Count(SecretSweepOutcome outcome) => Tenants.Count(t => t.Outcome == outcome);
}

internal static class SecretSweepReportMapper
{
    public static SecretSweepStepReport ToStepReport(SecretSweepResult result)
    {
        return new SecretSweepStepReport
        {
            Mode = result.Mode,
            StartedAt = result.StartedAt,
            CompletedAt = result.CompletedAt,
            CkTypesScanned = result.CkTypesScanned,
            EntitiesScanned = result.EntitiesScanned,
            EntitiesRewritten = result.EntitiesRewritten,
            ValuesRewritten = result.ValuesRewritten,
            PlaceholdersNormalized = result.PlaceholdersNormalized,
            SkippedConcurrentlyModified = result.SkippedConcurrentlyModified,
            Success = result.Success,
            Totals = SecretFormCountsReport.From(result.Totals),
            Slots = result.Slots.Select(s => new SecretSlotCountsReport
            {
                CkTypeId = s.CkTypeId,
                AttributePath = s.AttributePath,
                Counts = SecretFormCountsReport.From(s.Counts)
            }).ToList(),
            Cleared = result.Cleared.Select(SecretValueReference.From).ToList(),
            Unreadable = result.Unreadable.Select(SecretUnreadableValueReport.From).ToList(),
            Failures = result.Failures.Select(f => new SecretSweepFailureReport
            {
                CkTypeId = f.CkTypeId,
                RtId = f.RtId.ToString(),
                AttributePath = f.AttributePath,
                Reason = f.Reason
            }).ToList()
        };
    }
}
