using Npgsql;

namespace Flow4Tech.Infrastructure.Data;

/// <summary>
/// The restricted application role (<c>flow4tech_app</c>, created NOLOGIN by migration 0001). Provisioning gives it LOGIN and a password
/// taken from the environment at deploy time (never from the repo); the check proves the running credential cannot bypass RLS or change the schema.
/// </summary>
public static class AppRole
{
    public const string Name = "flow4tech_app";

    /// <summary>Idempotent: sets LOGIN and the password. Runs with the OWNER credential (CREATEROLE, admin on the role it created).</summary>
    public static async Task SetLoginAsync(string ownerConnectionString, string password, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        await using var connection = new NpgsqlConnection(ownerConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // ALTER ROLE takes no bind parameters: hand the value over through a transaction-local setting and quote it in the server with %L.
        await using (var stash = new NpgsqlCommand("SELECT set_config('flow4tech.provision_password', @password, true)", connection, transaction))
        {
            stash.Parameters.AddWithValue("password", password);
            await stash.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var alter = new NpgsqlCommand(
            "DO $$ BEGIN EXECUTE format('ALTER ROLE flow4tech_app LOGIN PASSWORD %L', current_setting('flow4tech.provision_password')); END $$",
            connection,
            transaction))
        {
            await alter.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Connects with the application credential and returns what makes it unfit for production (empty = restricted):
    /// superuser, BYPASSRLS, CREATEROLE/CREATEDB, owning tables, or able to create objects.
    /// </summary>
    public static async Task<IReadOnlyList<string>> FindProblemsAsync(string appConnectionString, CancellationToken cancellationToken = default)
    {
        var problems = new List<string>();
        await using var connection = new NpgsqlConnection(appConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using (var attributes = new NpgsqlCommand(
            "SELECT current_user, rolsuper, rolbypassrls, rolcreaterole, rolcreatedb, " +
            "(SELECT count(*) FROM pg_tables WHERE schemaname = 'public' AND tableowner = current_user) " +
            "FROM pg_roles WHERE rolname = current_user",
            connection))
        await using (var reader = await attributes.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                var user = reader.GetString(0);
                AddIf(problems, reader.GetBoolean(1), $"role {user} is a superuser");
                AddIf(problems, reader.GetBoolean(2), $"role {user} has BYPASSRLS");
                AddIf(problems, reader.GetBoolean(3), $"role {user} has CREATEROLE");
                AddIf(problems, reader.GetBoolean(4), $"role {user} has CREATEDB");
                AddIf(problems, reader.GetInt64(5) > 0, $"role {user} owns tables in schema public");
            }
        }

        await AddMembershipProblems(connection, problems, cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using var ddl = new NpgsqlCommand("CREATE TABLE flow4tech_ddl_probe (id int)", connection, transaction);
            await ddl.ExecuteNonQueryAsync(cancellationToken);
            problems.Add("the credential can create tables (DDL is not refused)");
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
            // Expected: the application credential has no DDL.
        }
        finally
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }

        return problems;
    }

    private static async Task AddMembershipProblems(NpgsqlConnection connection, List<string> problems, CancellationToken cancellationToken)
    {
        // Being a MEMBER of a stronger role (owner of the tables or of the database, or a built-in with broad powers) is as bad as being it.
        await using var command = new NpgsqlCommand(
            "SELECT current_user, " +
            "EXISTS (SELECT 1 FROM pg_tables WHERE schemaname = 'public' AND tableowner <> current_user AND pg_has_role(current_user, tableowner, 'MEMBER')), " +
            "pg_has_role(current_user, (SELECT pg_get_userbyid(datdba) FROM pg_database WHERE datname = current_database()), 'MEMBER'), " +
            "has_schema_privilege(current_user, 'public', 'CREATE'), " +
            "pg_has_role(current_user, 'pg_write_all_data', 'MEMBER'), " +
            "pg_has_role(current_user, 'pg_read_all_data', 'MEMBER'), " +
            "pg_has_role(current_user, 'pg_execute_server_program', 'MEMBER'), " +
            "pg_has_role(current_user, 'pg_write_server_files', 'MEMBER'), " +
            "pg_has_role(current_user, 'pg_read_server_files', 'MEMBER')",
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return;
        }

        var user = reader.GetString(0);
        AddIf(problems, reader.GetBoolean(1), $"role {user} is a member of the role that owns tables in schema public");
        AddIf(problems, reader.GetBoolean(2), $"role {user} is a member of the role that owns the database");
        AddIf(problems, reader.GetBoolean(3), $"role {user} has the CREATE privilege on schema public");
        var builtins = new[] { "pg_write_all_data", "pg_read_all_data", "pg_execute_server_program", "pg_write_server_files", "pg_read_server_files" };
        for (var i = 0; i < builtins.Length; i++)
        {
            AddIf(problems, reader.GetBoolean(4 + i), $"role {user} is a member of {builtins[i]}");
        }
    }

    private static void AddIf(List<string> problems, bool condition, string message)
    {
        if (condition)
        {
            problems.Add(message);
        }
    }
}
