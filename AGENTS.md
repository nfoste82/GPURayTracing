# Agent Instructions

Before making changes, read `AIDocs/00-index.md` and use it to select the project documentation relevant to the task. Avoid loading unrelated docs into context unless needed.

## Unity Test Runs

If Unity blocks a batch-mode test run because this project is already open, ask the user for explicit permission to close the editor automatically. After approval, request a normal Unity quit so it can shut down cleanly; do not force-kill Unity.
