using Xunit;

// Every test marshals onto the one shared WPF dispatcher thread (an Application can exist only once per process).
// Running test classes in parallel would interleave their awaits on that thread and make timing-based UI checks flaky,
// so the whole assembly runs serially.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
