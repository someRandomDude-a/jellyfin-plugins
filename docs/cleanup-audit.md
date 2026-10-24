# Focused cleanup — 2026-10-25

Removed ten inactive `.github/workflows/*.yml` files nested under component folders. GitHub discovers workflows only in the repository-root `.github/workflows`; the removed files assumed separate repository layouts and included obsolete standalone publication paths. Root tests, integration and release workflows remain. This removal does not claim that every historical standalone check is covered by current monorepo CI.

Preserved component scripts, catalog fixtures/publishers, API examples, compatibility adapters, historical verification records and unfinished plugins: they remain build/test inputs, migration contracts or useful evidence. No environment files, credentials, installed server data, user work, dependency directories or generated evidence were deleted. No other source file was established to be safely unused in this pass; this is not a claim of exhaustive dead-code elimination.
