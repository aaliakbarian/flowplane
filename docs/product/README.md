# FlowPlane Product Documentation

This directory contains public documentation about FlowPlane's product vision and scope as a control plane for heterogeneous data-pipeline platforms.

FlowPlane is in early development. These documents describe established intent, clarified current direction, and unresolved boundaries; they do not imply that every described capability is implemented or that the complete MVP scope is approved.

## Authority

- The [master architecture document](FlowPlane-Master-Architecture-and-Product-Design.md) defines overall product and architectural intent.
- Accepted Architecture Decision Records (ADRs), when present, are authoritative for the specific decisions they record.
- Derived product and architecture documents summarize narrower subjects and must remain consistent with those sources.
- Recommendations, assumptions, future provider candidates, and open questions are not architectural decisions.

See the [top-level documentation index](../README.md) for the complete documentation map.

## Current product direction

SSIS is the first data-pipeline provider. Other kinds of data-pipeline systems, including workflow-orchestration and data-transformation platforms such as Apache Airflow and dbt, are future candidates rather than committed implementations.

The intended MVP direction includes SSIS discovery, execution and monitoring, read-only SSIS Control Flow visualization, and Keycloak-based identity through standard OIDC. The complete MVP scope and its acceptance criteria remain unresolved.

## Documents

- [Master Architecture and Product Design](FlowPlane-Master-Architecture-and-Product-Design.md) — primary source for overall product and architectural intent.
- [Vision and Scope](vision-and-scope.md) — concise public summary of the data-pipeline control-plane vision, current SSIS focus, intended MVP direction, and future possibilities.

The master document is intentionally broader than the derived documents. Derived documents link to it rather than duplicating its full decision agenda.
