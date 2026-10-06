namespace Meshmakers.Octo.Backend.Jobs.Secrets;

/// <summary>
///     Identifies a secret sweep run in the run history (AB#5544).
/// </summary>
/// <param name="RunId">
///     The Hangfire job id of the job running the sweep; <c>null</c> outside a job (a random id is used).
/// </param>
/// <param name="TriggeredBy">User name of whoever started the run, or <c>null</c> (recurring, restore).</param>
public sealed record SecretSweepRunInfo(string? RunId, string? TriggeredBy = null);
