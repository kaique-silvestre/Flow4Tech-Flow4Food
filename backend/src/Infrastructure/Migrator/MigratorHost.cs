using Flow4Tech.Infrastructure.Data;

namespace Flow4Tech.Infrastructure.Migrator;

/// <summary>
/// Deploy step, configured only by environment variables:
/// <list type="bullet">
/// <item><c>MIGRATION_DATABASE_URL</c> or <c>ConnectionStrings__Migration</c> (required): OWNER credential.</item>
/// <item><c>APP_DB_PASSWORD</c> (optional): gives <c>flow4tech_app</c> LOGIN + this password (idempotent).</item>
/// <item><c>DATABASE_URL</c> or <c>ConnectionStrings__Default</c> (required unless SKIP_APP_CHECK=1): the APPLICATION credential, verified to be restricted.</item>
/// <item><c>SKIP_APP_CHECK</c> (<c>1</c>): explicit opt-out for a migrate-only run. Without it a missing application credential FAILS the run (the check is required by default).</item>
/// <item><c>MIGRATIONS_DIR</c> (optional): defaults to <c>migrations</c> next to the binaries.</item>
/// </list>
/// Exit codes: 0 ok, 1 failed (migration or unrestricted application credential), 2 bad configuration. Never prints a secret.
/// </summary>
internal static class MigratorHost
{
    public static async Task<int> RunAsync(Func<string, string?> environment, TextWriter output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(output);

        string owner;
        string? app;
        string directory;
        try
        {
            owner = DatabaseUrl.Resolve(environment("ConnectionStrings__Migration"), environment("MIGRATION_DATABASE_URL"))
                ?? throw new InvalidOperationException("No migration (owner) credential: set MIGRATION_DATABASE_URL (postgres://...) or ConnectionStrings__Migration.");
            app = DatabaseUrl.Resolve(environment("ConnectionStrings__Default"), environment("DATABASE_URL"));
            directory = environment("MIGRATIONS_DIR") is { Length: > 0 } configured ? configured : Path.Combine(AppContext.BaseDirectory, "migrations");
            if (!Directory.Exists(directory))
            {
                throw new InvalidOperationException($"Migrations directory not found (MIGRATIONS_DIR): {directory}");
            }
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException)
        {
            await output.WriteLineAsync($"migrate: configuration error: {ex.Message}");
            return 2;
        }

        try
        {
            var applied = await MigrationRunner.ApplyAsync(owner, directory, cancellationToken);
            await output.WriteLineAsync(applied.Count == 0 ? "migrate: schema already up to date" : $"migrate: applied {string.Join(", ", applied)}");

            if (environment("APP_DB_PASSWORD") is { Length: > 0 } password)
            {
                await AppRole.SetLoginAsync(owner, password, cancellationToken);
                await output.WriteLineAsync($"migrate: role {AppRole.Name} has LOGIN and the configured password");
            }
            else
            {
                await output.WriteLineAsync("migrate: APP_DB_PASSWORD not set, role provisioning skipped");
            }

            if (app is null)
            {
                if (environment("SKIP_APP_CHECK") is not ("1" or "true"))
                {
                    await output.WriteLineAsync("migrate: no application credential configured (DATABASE_URL / ConnectionStrings__Default), so the restricted-credential check cannot run; configure it, or set SKIP_APP_CHECK=1 for a migrate-only run (never in staging/production)");
                    return 1;
                }

                await output.WriteLineAsync("migrate: SKIP_APP_CHECK is set, restricted-credential check skipped");
                return 0;
            }

            var problems = await AppRole.FindProblemsAsync(app, cancellationToken);
            if (problems.Count > 0)
            {
                await output.WriteLineAsync($"migrate: the application credential is not restricted: {string.Join("; ", problems)}");
                return 1;
            }

            await output.WriteLineAsync("migrate: application credential verified (no superuser, no BYPASSRLS, no table ownership, no DDL)");
            return 0;
        }
        catch (Exception ex) when (ex is System.Data.Common.DbException or InvalidOperationException or IOException)
        {
            // Provider messages name the failing object, not the password; the connection strings are never printed.
            await output.WriteLineAsync($"migrate: failed: {ex.Message}");
            return 1;
        }
    }
}
