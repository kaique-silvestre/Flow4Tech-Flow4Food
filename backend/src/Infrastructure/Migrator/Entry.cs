namespace Flow4Tech.Infrastructure.Migrator;

// Explicit entry point (no top-level Program): the test project also sees the Api's Program type.
internal static class Entry
{
    private static Task<int> Main() => MigratorHost.RunAsync(Environment.GetEnvironmentVariable, Console.Out, CancellationToken.None);
}
