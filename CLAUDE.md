# CLAUDE.md - octo-bot-services

Job service of the Octo Mesh platform (Hangfire: dump, restore, export, import, secret sweep). Build and test with the
root `CLAUDE.md` rules (`dotnet build Octo.Bots.sln -c DebugL`, tests are TUnit: run the test executable of
`tests/Jobs.Tests` or `dotnet test`). Detailed behaviour is documented in `README.md`.

## Authorization (AB#4497)

| Surface | Required |
| --- | --- |
| Dump, restore, archive export / import, tus upload, artifact download | scope `octo_api` (full access) + tenant role `TenantManagement` (`BotServiceConstants.TenantJobManagementPolicy`; tus: `TusUploadAuthorization`) |
| Fixup script run | scope only |
| Secret sweep trigger / delete dump | scope + `SecretManagement`; sweep report / runs: read scope + `AdminPanelManagement` |
| Job status / delete (System API) | scope + job tenant binding (`JobTenantAccessGuard`) |
| Hangfire dashboard `/ui/jobs` | scope + caller of the **system tenant** (`tenant_id` claim) |

Rules:
- The role is checked on the caller's own token. A parent-tenant user (`[AllowParentTenantAdministration]`) needs
  `TenantManagement` in the parent tenant, not in the target tenant.
- The dump is a whole-tenant backup and bypasses row-level data permissions by design (AB#6734 covers export security
  contexts separately).
- Role policies probe both claim spellings (`role` and `ClaimTypes.Role`) via `BotAuthorizationPolicies.HasTenantRole`.
- Tenant gate defaults (`UserTokenEnforcement` / `ServiceTokenEnforcement` = Enforce) must not be overridden in
  `Program.cs`; `TenantJobRoleWiringTests` pins this.
- New tenant-wide data job = `TenantJobManagementPolicy` + a negative test per entry point.
