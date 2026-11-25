---
description: Upload state lives in the shared artifact store, never on a pod disk, so any replica can serve any request of an upload.
applies_to: src/BotServices/Tus*.cs, src/BotServices/Services/ArtifactTusStore.cs, src/Jobs/Services/*SharedUploadStore.cs
---

# tus uploads shared by all replicas

## tus uploads are shared by all replicas

The bot runs with more than one replica behind an ingress without affinity. Never store upload state on the pod's disk or
in process memory: the tus endpoint (`TusUploadEndpoint.MapBotTusUpload`) stores into the artifact store
(`ArtifactTusStore` → `ISharedUploadStore`, category `restore-staging`), and the consuming jobs read it from there by tus
file id (never a local path). Never copy a whole upload inside a request (the ingress times out, and two concurrent
assemblies remove each other's result): the job start only checks, the job reads (import) or assembles (restore). Test: `Jobs.Tests/Api/SharedUploadReplicaTests` (two hosts over one store: create on A, HEAD and PATCH on B, job
start on A, job on a third), `Jobs.Tests/Services/SharedUploadStoreTests`. Details: README "Shared tus uploads".
