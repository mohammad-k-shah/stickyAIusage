# Security policy

## Supported versions

Fixes go into the newest release only. Please update before reporting: run the install line again, or
`winget upgrade MohammadKShah.StickyAIUsage` once the app is listed there.

| Version | Supported |
|---|---|
| 2.0.x | Yes |
| 1.x | No |

## Reporting a problem

Please don't open a public issue for a security problem. Report it privately through GitHub:
[**Report a vulnerability**](https://github.com/mohammad-k-shah/stickyAIusage/security/advisories/new) (the
repository's Security tab). You'll get a first answer within a few days; a confirmed problem is fixed in a patch
release and credited to you in its notes unless you'd rather not be named.

## What's in scope

Sticky AI Usage runs on your computer and has no server of its own. What it reads, writes, starts and sends is listed
in [Data sources](docs/DATA-SOURCES.md). Reports about any of these are welcome:

- the Windows and macOS apps, including how they read Claude Code's session files and the companion plugin's files;
- the Legacy and Automatic usage sources, which read Claude Code's sign-in token (read-only) to ask Claude's usage
  service;
- the companion Claude Code plugin in `plugin/`;
- the install scripts, the Windows installer and the release workflow (checksums, provenance).

Out of scope: problems in Claude Code, Claude's services or other projects themselves (report those to their makers),
and anything that needs someone to already control your user account.
