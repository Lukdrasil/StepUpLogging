using Xunit;

// Several integration tests drive a TestServer and rely on ambient global state — notably
// Serilog's static Log.Logger and Activity.Current. xunit runs test classes in parallel by
// default, which races those globals and makes the suite fail ~1 run in 5 on a clean checkout.
// The suite is small (~7s serial), so we disable parallelization for a deterministic regression
// gate. Revisit if a future split isolates the global-state tests into their own serial collection.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
