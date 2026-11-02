# Anti-errors
- `dotnet list package --vulnerable` prints localized text and exits 0 on findings -> grep for the English phrase silently passes under a pt-BR locale, and restore errors (NU1903) hide the report -> set `DOTNET_CLI_UI_LANGUAGE=en` locally and also fail on nonzero exit.
- zsh: `${PIPESTATUS[0]}` is empty -> use `$pipestatus[1]` or avoid pipes when checking exit codes.
