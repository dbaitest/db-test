using DbTest.Core;

namespace DbTest.Integration.Tests;

/// <summary>
/// Assembly-level setup: stops every container when the run ends.
/// </summary>
[TestClass]
public class AssemblyLifecycle
{
    [AssemblyCleanup]
    public static async Task CleanupAsync()
    {
        await EngineFixture.DisposeAllAsync();
    }
}
