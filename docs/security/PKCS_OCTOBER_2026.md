# Pkcs dependency security repair

Issue #289 starts from main `2e620a4856ab65f80706ba6f5b2c3da6b2ea74fd`. EF SqlServer10.0.12 resolves SqlClient6.1.6, whose dependency minimum selects Pkcs9.0.11 without an explicit override.

Microsoft published two denial-of-service advisories on2026-10-13: [CVE-2026-84008](https://github.com/dotnet/announcements/issues/468) affects all platforms; [CVE-2026-70162](https://github.com/dotnet/announcements/issues/454) affects Windows. Both specify9.0.21 as the patched9.x package. Persistence explicitly references that patched package so its consumers resolve it through the existing dependency graph.

This changes one dependency reference. Existing build/test/format, resolved dependency inspection and NuGet audit must verify the repair. Required exact-head CI must finish; new package code requires genuine SQL/runtime regression verification. Application source, provider configuration, schema, permissions and runtime proof logic stay byte equal to the main parent.

No customer data, deployment, secret store or model is used to verify the package update. This security repair can be merged independently of the active automatic group-note work in PR #288. Full product, actual model, dedicated Medcom/Novo business queries and installer/deployment qualification remain separate unfinished gates.
