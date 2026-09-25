# Fixtures

Redacted or synthetic log samples used by the unit tests.

Rules:
- No real hostnames, usernames, domain names, IP addresses or customer identifiers.
- Redaction is reviewed by a second person before commit.
- One folder per scenario, named for the rule ids it exercises, e.g. `orphaned-ts-TS-001/`.
- Keep files small — trim to the last few thousand lines around the failure.

Collect these from real failed machines first. The rules cannot be validated without them,
and synthetic logs will not catch real-world format drift.
