using DbAiTest.MSTest;

// Tests share one container and one schema: they run one at a time.
[assembly: DoNotParallelize]

namespace Sample.Database.Tests;

[TestClass]
public static class TestRun
{
    // Removes the containers as soon as the run ends.
    [AssemblyCleanup]
    public static Task Cleanup() => DbAiTestRun.DisposeAllAsync();
}
