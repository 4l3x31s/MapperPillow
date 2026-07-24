// The interception counter in MapperPillowTelemetry is process-wide shared state,
// so run tests sequentially to keep count assertions deterministic.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
