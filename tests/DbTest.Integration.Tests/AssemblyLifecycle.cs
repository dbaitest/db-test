using DbTest.MSTest;

// The engines share one container each, and one schema per container: tests that ran in
// parallel would see each other's rows and each other's DDL.
[assembly: DoNotParallelize]

namespace DbTest.Integration.Tests;

/// <summary>
/// Assembly-level cleanup: removes every container as soon as the run ends and flushes the
/// timing file.
/// </summary>
[TestClass]
public static class AssemblyLifecycle
{
    [AssemblyCleanup]
    public static async Task CleanupAsync()
    {
        await DbTestRun.DisposeAllAsync();
    }
}
