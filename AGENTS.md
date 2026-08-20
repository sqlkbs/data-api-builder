# 🛠️ Global AI Coding Guardrails (Karpathy Skills)
<!-- NOTE: Do not remove or modify the behavioral rules in this section. Append project memories below the divider. -->

Behavioral guidelines to reduce common LLM coding mistakes. Merge with project-specific instructions as needed.

**Tradeoff:** These guidelines bias toward caution over speed. For trivial tasks, use judgment.

## 1. Think Before Coding

**Don't assume. Don't hide confusion. Surface tradeoffs.**

Before implementing:
- State your assumptions explicitly. If uncertain, ask.
- If multiple interpretations exist, present them - don't pick silently.
- If a simpler approach exists, say so. Push back when warranted.
- If something is unclear, stop. Name what's confusing. Ask.

## 2. Simplicity First

**Minimum code that solves the problem. Nothing speculative.**

- No features beyond what was asked.
- No abstractions for single-use code.
- No "flexibility" or "configurability" that wasn't requested.
- No error handling for impossible scenarios.
- If you write 200 lines and it could be 50, rewrite it.

Ask yourself: "Would a senior engineer say this is overcomplicated?" If yes, simplify.

## 3. Surgical Changes

**Touch only what you must. Clean up only your own mess.**

When editing existing code:
- Don't "improve" adjacent code, comments, or formatting.
- Don't refactor things that aren't broken.
- Match existing style, even if you'd do it differently.
- If you notice unrelated dead code, mention it - don't delete it.

When your changes create orphans:
- Remove imports/variables/functions that YOUR changes made unused.
- Don't remove pre-existing dead code unless asked.

The test: Every changed line should trace directly to the user's request.

## 4. Goal-Driven Execution

**Define success criteria. Loop until verified.**

Transform tasks into verifiable goals:
- "Add validation" → "Write tests for invalid inputs, then make them pass"
- "Fix the bug" → "Write a test that reproduces it, then make it pass"
- "Refactor X" → "Ensure tests pass before and after"

For multi-step tasks, state a brief plan:
```
1. [Step] → verify: [check]
2. [Step] → verify: [check]
3. [Step] → verify: [check]
```

Strong success criteria let you loop independently. Weak criteria ("make it work") require constant clarification.

---

**These guidelines are working if:** fewer unnecessary changes in diffs, fewer rewrites due to overcomplication, and clarifying questions come before implementation rather than after mistakes.

---

# Custom Data API Builder (DAB) Fork - Architecture & Developer Guidelines

This repository is a custom fork of Microsoft's open-source **Data API builder (DAB)** (`Azure/data-api-builder`). 
All AI agents, coding assistants, and human developers working in this workspace **MUST** strictly adhere to the branching, isolated implementation, and architectural patterns described below.

---

## 1. Git & Branching Strategy

To ensure seamless future merges and rebases from `upstream/main` (`Azure/data-api-builder`), follow these strict branching rules:

1. **`main` Branch is Immutable:** 
   - Never commit directly to `main`. 
   - The `main` branch serves solely as a clean, untouched mirror of official Microsoft releases (`upstream/main`).
2. **Feature Isolation (`feature/*`):** 
   - Work on specific custom capabilities in isolated feature branches (e.g., `feature/mssql-geometry`, `feature/tenant-schema-registry`).
3. **Release Branch (`release/custom-build`):** 
   - All feature branches are merged into `release/custom-build` for compiling Docker images and binaries.
4. **Rebase Over Merge:** 
   - When updating from upstream, fetch `upstream/main`, update local `main`, and run `git rebase main` on individual feature branches to keep the commit history clean.

---

## 2. Code Isolation Protocol (Extension-First Pattern)

To prevent severe merge conflicts when core DAB files are modified by upstream releases, **do not write heavy inline business logic directly inside core DAB classes** (e.g., `MsSqlQueryBuilder.cs`, `SqlMetadataProvider.cs`, `RuntimeConfigProvider.cs`).

### Architectural Rules for New Code:
- **Zero Heavy Inline Modifications:** Keep calls in core DAB files to single-line hooks/extension invocations.
- **Isolate Logic in Dedicated Files:** Place custom implementation logic in new, isolated extension/service files (e.g., `MsSqlSpatialExtensions.cs`, `TenantSchemaRegistryService.cs`) under dedicated namespaces.
- **C# Extension Methods:** Prefer C# extension methods or partial classes over direct method signature alterations.

#### Example Call-Site Injection (`MsSqlQueryBuilder.cs`):
```csharp
// MINIMAL TOUCHPOINT IN CORE CLASS:
public string BuildColumnProjection(string columnName, string dataTypeName)
{
    // Custom logic redirected via extension method:
    if (dataTypeName.IsCustomHandledType()) 
    {
        return dataTypeName.ToCustomProjection(columnName);
    }
    
    return $"[{columnName}]";
}
```

---

## 3. Key Custom Extensions & Architectural Features

When generating code or refactoring features, be aware of the following custom systems implemented in this fork:

### A. MS SQL Spatial / Geometry Support
- **Types:** Maps `geometry` and `geography` SQL types to `System.String`.
- **Query Building:** Converts projections to `STAsText() AS [Column]` (WKT) or `.ToGeoJson()`.
- **Mutations:** Wraps incoming text parameters with `geometry::STGeomFromText(@param, 4326)` during `INSERT`/`UPDATE`.

### B. Dynamic Tenant Schema Registry (`sys_TenantSchemaFields`)
- **Metadata Management:** Dynamic tenant JSON columns are registered in `dbo.sys_TenantSchemaFields`.
- **In-Memory Caching:** Registry metadata is loaded into a thread-safe `ConcurrentDictionary` on startup via `ITenantSchemaRegistryService`. **Never issue a database roundtrip per API request to fetch schema mapping.**
- **Query Interception:** `MsSqlQueryBuilder` inspects `HttpContext` claims for `TenantId`, retrieves registered custom JSON fields from memory, and automatically appends `JSON_VALUE(...) AS [Alias]` or `LEFT JOIN` lookup definitions to the SQL AST.

### C. Physical-to-Business Key Translation
- **Read Operations:** Resolves relational FK IDs to business codes via SQL `LEFT JOIN` AST generation.
- **Write Operations:** Translates business codes (`"ABC"`) to internal integer IDs (`5`) using in-memory lookup caching prior to SQL parameter construction.

---

## 4. Coding & Testing Standards for AI Assistants

When asked to generate code, refactor, or fix bugs in this codebase:

1. **Verify Target File Touchpoints:** Identify if you are touching core DAB files. If so, minimize the edit to an invocation call and place the core implementation in a new file under `Azure.DataApiBuilder.Core/Custom/`.
2. **Preserve Performance:** Always optimize for zero extra database roundtrips. Prefer C# in-memory caching (`IMemoryCache` or `ConcurrentDictionary`) and SQL-level AST generation.
3. **Async Standard:** Ensure all database calls utilize `async`/`await` patterns natively supported by `Microsoft.Data.SqlClient` and Dapper.
4. **Unit Tests:** Always create corresponding unit tests under `Azure.DataApiBuilder.Service.Tests` whenever creating a new custom extension file.