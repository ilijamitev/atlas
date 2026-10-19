---
description: Finish a step. Review the diff with the reviewer agent, then write the step summary and a commit message.
---

The current step is finished. Do the following in order, and change no files.

1. Run `git status` and `git diff HEAD --stat` to see the step's changes.
2. Ask the `reviewer` subagent to review them, and wait for its report.
3. Write the step summary:
   - **What changed:** the behaviour, not a file list.
   - **Why:** the requirement or decision behind it (ticket AC, GC-2026-0814 point, Annex B, Teams thread).
   - **Alternatives rejected:** each with the reason it lost.
   - **30-second explanation:** how to explain this step in the interview.
   - **Risks and reviewer findings:** flag them; don't fix them.
   - **Suggested commit message:** an imperative subject under 72 characters, then a short body.
