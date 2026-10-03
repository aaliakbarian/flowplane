# FlowPlane

FlowPlane is an early-stage open-source control plane for heterogeneous data-pipeline platforms. It is intended to provide a consistent way to discover, schedule, execute, and monitor pipelines while each provider retains its native runtime and platform-specific behavior.

## Provider direction

SQL Server Integration Services (SSIS) is the first provider. It is a concrete starting point, not the architectural foundation of FlowPlane.

Future provider candidates include data-pipeline, workflow-orchestration, and data-transformation platforms such as Apache Airflow and dbt. These are candidates for investigation, not implemented features or commitments about which provider comes next.

## Architecture

FlowPlane separates a cross-platform **Control Plane** from a provider-specific **Execution Plane**. Provider adapters translate between FlowPlane concepts and external platforms without requiring every provider to expose the same model or capabilities.

The current MVP direction includes SSIS discovery and execution monitoring, a read-only SSIS Control Flow view using React Flow, and authentication through Keycloak using standard OpenID Connect (OIDC). React Flow is a presentation technology, not the canonical backend workflow model, and the complete MVP scope remains to be defined.

## Project status

FlowPlane is in early development. APIs, deployment guidance, provider contracts, and the complete MVP scope are not yet stable.

## Documentation and contributing

- [Documentation](docs/README.md)
- [Master Architecture and Product Design](docs/product/FlowPlane-Master-Architecture-and-Product-Design.md)
- [Contributing](CONTRIBUTING.md)
- [Code of Conduct](CODE_OF_CONDUCT.md)

## License

Apache License 2.0. See [LICENSE](LICENSE).
