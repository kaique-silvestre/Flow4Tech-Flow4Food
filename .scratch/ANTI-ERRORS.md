# Anti-errors
- `dotnet list package --vulnerable` prints localized text and exits 0 on findings -> grep for the English phrase silently passes under a pt-BR locale, and restore errors (NU1903) hide the report -> set `DOTNET_CLI_UI_LANGUAGE=en` locally and also fail on nonzero exit.
- zsh: `${PIPESTATUS[0]}` is empty -> use `$pipestatus[1]` or avoid pipes when checking exit codes.
- CA2100 (warnings are errors) fires on any `new NpgsqlCommand(x, ...)` where `x` is a variable, even a loop over literals -> use string literals at the call site; for running repo migration files use the single justified `SuppressMessage` in `MigrationRunner` (test project has CA2100 off in its own .editorconfig).
- RLS: after a transaction that used `set_config('app.organizacao_id', .., true)`, `current_setting(.., true)` returns '' (not NULL) -> `::uuid` cast errors for the next user of the connection -> always `NULLIF(current_setting(..), '')` (done in `app_organizacao_id()`).
- macOS `sed -i ''` has no `\b` and silently does nothing -> use `perl -pi -e 's/\bX\b/Y/g'` for word-boundary renames.
- Test project with a static member named `Org` fails CS0118 ('Org' is a namespace, from BouncyCastle via Testcontainers) -> name it `OrgId`; and private test helpers returning an interface trip CA1859 -> make the helper `public static`.
- Two Exe/Web projects with top-level statements both define `Program` -> a test project referencing both gets CS0433 on `WebApplicationFactory<Program>` -> give the second project an explicit `Main` in a named class (see `Migrator/Entry.cs`).
- CA1054 fires on any `string` parameter whose name contains `url`/`uri` -> name it `value`/`fallback`.
