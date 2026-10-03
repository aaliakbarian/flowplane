# FlowPlane Architecture Documentation

This directory describes FlowPlane's current accepted architecture. FlowPlane is in early development, so the architecture documentation must distinguish established boundaries from recommendations, assumptions, and open questions.

## Authority

- The [master architecture document](../product/FlowPlane-Master-Architecture-and-Product-Design.md) defines overall product and architectural intent.
- Accepted [Architecture Decision Records](../adr/README.md), when present, are authoritative for the specific decisions they record.
- Documents in this directory describe the current architecture and must remain consistent with accepted ADRs.
- Recommendations, assumptions, examples, and candidate designs are not decisions unless the project explicitly accepts them.

## Current documents

- [Architecture Overview](overview.md) — established system boundaries, data ownership, provider role, platform-independence principle, and unresolved architectural areas.

Additional focused architecture documents should be added only as their subjects become sufficiently established. They should link to governing ADRs and the master architecture document instead of duplicating decision history.

## Maintaining these documents

When an accepted architectural decision changes, preserve its history through a superseding ADR and update the affected current-state architecture documents. Do not silently convert an open question or recommendation into accepted architecture.

See the [top-level documentation index](../README.md) for the broader documentation map.
