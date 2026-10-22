# ZeroPipeline Adapter Skeleton

## Scope

Implement the eight declared InvoiceFlow pipeline stages as real ZeroPipeline nodes. Build graphs from the project-owned `PipelineRecipe`, create run-scoped executors, reuse existing application services where available, and require explicit stage ports for business behavior that has not been migrated yet. No business stage may silently succeed as a no-op.

## Tasks

1. Define typed run packets and stage payloads; add the eight node adapters with explicit required stage ports and failure/cancellation handling.
2. Add recipe-to-`PipelineGraph` construction using ZeroPipeline `NodeRegistry`/`RecipeGraphBuilder`, validate packet types, and create an isolated executor/context for each run.
3. Add the Application DI composition extension and fail fast when required stage ports are missing.
4. Add real-executor tests for all eight stages, typed data flow, failure output, cancellation, invalid connections, and missing dependencies; run focused tests and the full solution suite.

## Acceptance Criteria

- All eight `BuiltInNodeTypes` resolve to executable `PipelineNode` implementations.
- Recipe validation happens before graph construction; graph connections use compatible CLR payload types.
- Run state is not shared between executors.
- Missing business stages are explicit required ports, never no-op implementations.
- Tests use the real ZeroPipeline executor with deterministic fake stage ports.