namespace Meshmakers.Octo.Backend.BotServices.SystemApi.v1;

/// <summary>
///     REST deprecation of the job polling API <c>system/v1/jobs</c> (AB#8447, Feature AB#5694 F3.6;
///     <c>octo-common-services/docs/rest-deprecation.md</c>). Dump, restore and archive data export/import run as
///     asynchronous CK methods of <c>System.Bot/TenantRepository</c> with an operation handle (<c>runtime.operations</c>),
///     which replaces polling a job id.
/// </summary>
/// <remarks>
///     The dates come from configuration (<c>RestDeprecation:Waves:BotJobs1:Since|Sunset</c>), not from code. Until a
///     maintainer sets them, the endpoints answer the <c>Link</c> header, are <c>deprecated</c> in OpenAPI and are counted
///     in <c>octo.rest.deprecated.calls</c>, but carry no <c>Deprecation</c> / <c>Sunset</c> header. Do not set a sunset
///     before the remaining job types that report through <c>system/v1/jobs</c> (ImportRt, ImportCk, the RT exports, the
///     fixup run, the secret sweep) have an operation handle too.
/// </remarks>
public static class BotRestDeprecation
{
    /// <summary>Wave of the job polling API.</summary>
    public const string JobsWave1 = "BotJobs1";

    /// <summary>Documentation of the asynchronous methods that replace job polling.</summary>
    public const string AsyncMethodsLink =
        "https://docs.meshmakers.cloud/docs/technologyGuide/dataAccess/reference/backupRestore#async-methods";
}
