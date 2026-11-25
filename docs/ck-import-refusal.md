---
description: The CK import jobs refuse System models, fail at once without retry, and keep their job signatures.
applies_to: src/Jobs/Commands/ImportCkModelCommand.cs, src/Jobs/Jobs/ImportModelJob.cs
---

# CK imports of service-managed models

## CK imports of service-managed models are refused

`ImportModelJob.ImportCkAsync` / `ImportCkBatchAsync` call `ImportCkModelCommand`, which calls the engine's caller overload
`ITenantContext.ImportCkModelAsync(CkCompiledModelRoot)`. In engine-mongodb that overload refuses every `System` /
`System.*` model (`ServiceManagedCkModelImportRefusedException`, code `CK_SERVICE_MANAGED_MODEL`) before it writes anything.
The command logs one WARN (model id, code, never the content) and rethrows; both job methods carry
`AutomaticRetry(ExceptOn = [typeof(ServiceManagedCkModelImportRefusedException)])`, so the job is `Failed` at once. The job
signatures are unchanged on purpose: a job enqueued before the update runs into the same refusal. Batch rule: per model, in
order; the refused model stops the batch, earlier models stay imported, no cache stream is cleared. Never add a bypass
here: service-managed models come only through the owning service's embedded import. Tests:
`tests/Jobs.Tests/Jobs/ImportModelJobServiceManagedRefusalTests`.
