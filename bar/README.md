# CCCOG-Bar workspace

This directory contains the platform-neutral Rust core and the later thin
Windows shell.  The first implementation wave is intentionally read-only: it
parses bounded CCCG/provider files and never calls a provider or writes a
dispatch file.

## Scope boundary

The core reports explicit token counts and provider quota windows only.  It has
no cost fields, money arithmetic, pricing table, LiteLLM/OpenRouter dependency,
or inference request.  Quota HTTP polling is isolated behind an injected
read-only client; prompts, transcripts, paths, and credentials are never sent
as request data and credentials are never written back.
