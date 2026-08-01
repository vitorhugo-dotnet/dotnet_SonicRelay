# ADR 0002: Use Identity opaque bearer tokens

- Status: Superseded by [ADR 0006](0006-remove-identity.md)
- Date: 2026-07-04

## Context

Windows and Flutter clients need token-based authentication without browser cookie assumptions, while user and role state needs durable storage.

## Decision

Use ASP.NET Core Identity API endpoints, EF Core PostgreSQL stores and Identity's opaque bearer/refresh token scheme. API and WebSocket requests authenticate with the bearer access token.

## Consequences

The project avoids custom password/token issuance code and clients must treat tokens as opaque. Built-in bearer tokens are self-contained, so the current logout endpoint cannot revoke an issued token; short lifetimes and client-side deletion are required until revocation is added.

**Update (issue #26 Phase 4):** ASP.NET Core Identity and this bearer/refresh scheme were removed once both clients migrated to device-identity authentication (ADR 0005). See [ADR 0006](0006-remove-identity.md).
