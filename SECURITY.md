# Security Policy

## Reporting a vulnerability

Please do **not** report security issues through public GitHub issues.

Instead, report them privately through GitHub's
[security advisory](https://github.com/eryph-org/eryph-mcp/security/advisories/new)
form, or by email to **security@dbosoft.eu**. We will acknowledge your report
and keep you informed about the progress towards a fix.

## Scope and threat model

The eryph MCP server runs **locally** over stdio with the eryph client
credentials of the user who starts it. It does not open a network listener and
does not store credentials. Keep in mind when operating it:

- An MCP client connected to this server can create, run, delete and run
  commands in catlets with the configured eryph client's permissions. Only
  connect it to clients you trust.
- `catlet_exec` runs arbitrary commands in a guest. The server authorizes an
  ephemeral SSH key (generated per process, never written to disk, with an
  8-hour TTL) in the target catlet to do so.
- Without a configured eryph client the server falls back to the local system
  client, which requires an elevated process.
