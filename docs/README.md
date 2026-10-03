# FlowPlane Documentation

FlowPlane is an early-stage open-source control plane for heterogeneous data-pipeline platforms.

This index covers the public documentation currently available in the repository. Additional documents should be added incrementally as product scope, architectural decisions, and implemented behavior become established.

## Documentation authority

1. The [master architecture document](product/FlowPlane-Master-Architecture-and-Product-Design.md) defines overall product and architectural intent.
2. Accepted Architecture Decision Records (ADRs) are authoritative for the specific decisions they record.
3. Architecture documents describe the current accepted architecture and must remain consistent with accepted ADRs.
4. Product summaries, guides, and reference documents explain narrower subjects without introducing architectural decisions.
5. Recommendations, assumptions, examples, future candidates, and open questions are not accepted decisions.

The public derived documents also reflect clarified current product direction. They do not replace the master architecture document, create an ADR, or finalize the MVP scope.

## Current direction

- SSIS is the first data-pipeline provider and the current integration focus; it is not the platform foundation.
- Apache Airflow, dbt, and other data-pipeline platforms are future provider candidates, not implementation commitments.
- The intended MVP includes read-only SSIS Control Flow monitoring and exploration using React Flow. React Flow is not the canonical backend workflow model.
- Keycloak is the intended MVP identity provider through standard OpenID Connect (OIDC); detailed authentication and authorization architecture remains unresolved.

## Documentation map

### Product

- [Product documentation](product/README.md)
- [Vision and scope](product/vision-and-scope.md)
- [Master architecture and product design](product/FlowPlane-Master-Architecture-and-Product-Design.md)

### Architecture

- [Architecture documentation](architecture/README.md)
- [Architecture overview](architecture/overview.md)

### Architecture decisions

- [ADR process](adr/README.md)
- [ADR template](adr/template.md)

No actual ADR decision records exist yet.

## Contributing

See the repository's [contribution guide](../CONTRIBUTING.md) and [code of conduct](../CODE_OF_CONDUCT.md). Contributor documentation is still being established alongside the project.

Public documentation must remain understandable without relying on files under `.ai/`, which are development guidance rather than architectural authority.
