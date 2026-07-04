# ADR 0003: Split durable and ephemeral storage

- Status: Accepted
- Date: 2026-07-04

## Context

Users, devices and session history require relational durability, while short-lived join-code lookup needs automatic expiry and fast replacement.

## Decision

Store Identity and domain entities in PostgreSQL through EF Core. Store HMAC-derived session-code lookup keys and the current-code pointer in Redis with absolute TTLs.

## Consequences

Both PostgreSQL and Redis are readiness dependencies. Operators must back up PostgreSQL and protect both credentials. Redis loss invalidates active join codes but does not erase durable sessions; code rotation can restore lookup state.
