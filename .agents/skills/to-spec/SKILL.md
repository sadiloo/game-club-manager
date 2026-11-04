---
name: to-spec
description: "Turn the current conversation into a spec file at docs/specs/<ID>-<slug>.md. No interview, and no issue tracker."
disable-model-invocation: true
---

Local edit (npx skills update would overwrite this): specs are files in
docs/specs/, not GitHub issues.

Write `docs/specs/<ID>-<slug>.md` using `docs/specs/_TEMPLATE.md`.
Number every rule and give each a worked example. Do not publish to an
issue tracker.

Do not interview. Synthesize what the conversation already established.
Use docs/CONTEXT.md for domain words and docs/adr/ for past decisions.
Leave "Open questions" empty only when they are actually resolved;
otherwise Status stays Draft.
