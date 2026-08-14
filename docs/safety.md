# Safety and compliance boundaries

CCCG is an interoperability research harness, not a stealth replacement.

## The project may

- translate inputs supplied by an authorized local test host;
- use official provider clients and APIs with the user's own authorization;
- compare behavior across deterministic mock, Codex, and xAI backends;
- document protocol observations and sanitized test evidence;
- build a standalone executable in the CCCG workspace.
- save caller-authorized prompt and visible response text in an explicit local
  debug dataset that is excluded from source control.

## The project must not

- modify, redistribute, decrypt, decompile, or patch proprietary binaries;
- impersonate Anthropic, Claude, OpenAI, Codex, xAI, or Grok;
- hide the actual provider/model from the tester;
- copy or replay another product's session tokens or API credentials;
- bypass authentication, rate limits, billing, licensing, safety controls, or
  update mechanisms;
- install into another product without a separate, explicit, reversible test;
- ship proprietary installers, internal source, secrets, or user transcripts.

Local content capture is not publication permission. A captured dataset may
contain secrets or personal information even though thinking and tool payloads
are excluded. Before sharing a bug report, reproduce with synthetic messages or
manually sanitize an exported excerpt; never publish `content.jsonl` directly.

## Publication checklist

- repository visibility verified as private before and after push;
- no secrets in Git history or working tree;
- no proprietary binaries or copied source;
- README states this is unofficial and names actual backend attribution;
- test fixtures contain synthetic messages only;
- live calls are opt-in and use caller-owned credentials;
- no instructions for evading platform enforcement.

This checklist reduces technical and contractual risk; it is not legal advice or
a guarantee that every host integration is permitted under every agreement and
jurisdiction.
