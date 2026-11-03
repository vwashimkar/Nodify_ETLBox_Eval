namespace Nodify_ETLBox.Tests;

/// <summary>
/// Runs a call that reaches ETLBox with a time limit, so a hang fails the test
/// instead of freezing the whole test run.
/// </summary>
internal static class TestTimeout
{
    public static readonly TimeSpan Default = TimeSpan.FromSeconds(10);

    public static async Task<T> Run<T>(Func<Task<T>> call, TimeSpan? limit = null)
    {
        // Task.Run so that a call that blocks before its first await is also covered.
        var task = Task.Run(call);
        var finished = await Task.WhenAny(task, Task.Delay(limit ?? Default));
        if (finished != task)
        {
            throw new TimeoutException($"The call did not finish within {(limit ?? Default).TotalSeconds:0} s. It probably hangs.");
        }

        return await task;
    }

    public static Task Run(Func<Task> call, TimeSpan? limit = null)
        => Run(async () => { await call(); return true; }, limit);
}
