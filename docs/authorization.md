---
description: HTTP authorization of the job service: policies, tenant roles, Hangfire dashboard access and tenant gate defaults.
applies_to: src/BotServices/Configuration/BotAuthorizationPolicies.cs, src/BotServices/*Authorization*.cs, src/BotServices/HangfireDashboard*.cs, src/BotServices/*Api/**
---

# Authorization and tenant job gates

## Authorization

| Surface | Required |
| --- | --- |
| Dump, restore, archive export / import, fixup script run, tus upload, artifact download | scope `octo_api` (full access) + tenant role `TenantManagement` (`BotServiceConstants.TenantJobManagementPolicy`; tus: `TusUploadAuthorization`) |
| Secret sweep trigger / delete dump | scope + `SecretManagement`; sweep report / runs: read scope + `AdminPanelManagement` |
| Job status / delete (System API) | scope + job tenant binding (`JobTenantAccessGuard`) |
| Hangfire dashboard `/ui/jobs` | scope + caller of the **system tenant** (`tenant_id` claim) **and** tenant role `TenantManagement` (`HangfireDashboardAccessMiddleware` + `HangfireDashboardAuthorizationFilter`; others get 403) |

Rules:
- The role is checked on the caller's own token. A parent-tenant user (`[AllowParentTenantAdministration]`) needs
  `TenantManagement` in the parent tenant, not in the target tenant.
- The dump is a whole-tenant backup and bypasses row-level data permissions by design (see "Export jobs run as the
  caller" below); the runtime-data exports are the opposite.
- Role policies probe both claim spellings (`role` and `ClaimTypes.Role`) via `BotAuthorizationPolicies.HasTenantRole`.
- Tenant gate defaults (`UserTokenEnforcement` / `ServiceTokenEnforcement` = Enforce) must not be overridden in
  `Program.cs`; `TenantJobRoleWiringTests` pins this.
- New tenant-wide data job = `TenantJobManagementPolicy` + a negative test per entry point.
