# FlowPlane Architecture Decision Records

Architecture Decision Records (ADRs) preserve the context, accepted choice, alternatives, and consequences of architecturally significant decisions.

The [master architecture document](../product/FlowPlane-Master-Architecture-and-Product-Design.md) defines FlowPlane's overall product and architectural intent. An accepted ADR is authoritative only for the specific decision it records. Current-state [architecture documents](../architecture/README.md) must remain consistent with accepted ADRs.

## When to create an ADR

Create an ADR when the project accepts an architecturally significant decision and preserving its rationale and consequences will help future contributors understand or safely change the system.

Do not create an ADR merely because a topic appears in the master architecture document. In particular:

- recommendations, assumptions, examples, and open questions are not decisions;
- candidate technologies are not selected technologies;
- minor implementation details do not require ADRs unless they establish a durable architectural constraint;
- retrospective ADRs should be selective, not an automatic transcription of every existing principle.

No actual decision record is created by this initial documentation batch.

## Authority and history

- A draft or otherwise unaccepted ADR is not architectural authority.
- An accepted ADR is authoritative within its stated scope.
- Architecture documents describe the current accepted design and link to the relevant ADRs.
- When an accepted decision changes, create a new ADR that supersedes the earlier record.
- Keep superseded ADRs as history; do not silently rewrite their decisions or rationale.

The project has not yet established the complete ADR status vocabulary, numbering convention, or acceptance authority. Those documentation-governance questions must be resolved before the first decision record is accepted.

## ADR contents

Use the [ADR template](template.md). Each record should contain:

- status;
- context;
- the decision;
- alternatives considered;
- consequences;
- supersedes and superseded-by relationships.

Keep an ADR focused on one decision. Link to the master architecture document and relevant current-state architecture documents rather than copying them.

See the [top-level documentation index](../README.md) for the complete documentation map.
