# Security

This app holds people's financial records, so security reports are taken seriously.

## Reporting a problem

Please **don't** open a public issue. Use GitHub's private reporting instead: the repository's **Security** tab, then
**Report a vulnerability**. Include what you found, how to reproduce it and what it could expose.

You'll get a reply within a week. Please give a reasonable time for a fix before telling anyone else.

## Supported versions

Only the latest code on the `main` branch is supported.

## If you host your own copy

- Keep connection strings and API keys out of the repository: use user-secrets in development and
  `appsettings.Production.json` (or environment variables) on the server only.
- Never copy `src/`, `tests/` or `.git` into the web root.
- Use HTTPS: the login cookie is sent only over secure connections.
- Give the app's SQL login only the rights it needs, and keep backups.
