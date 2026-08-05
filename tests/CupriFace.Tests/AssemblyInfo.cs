using Xunit;

// Run tests sequentially. Several tests spin up background render threads (ThreadedRenderer /
// ThreadedPresenter) and assert on timing; xUnit's default class-parallelism starves those threads
// under load and makes the waits flaky. The whole suite runs in well under a second, so serial is fine.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
