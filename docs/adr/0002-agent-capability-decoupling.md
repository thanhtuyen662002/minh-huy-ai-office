# ADR-0002: Agents Depend on Capabilities

Status: Accepted

Agent roles request semantic capabilities rather than specific model names, SQL tables, ERP forms or provider APIs. Registries/adapters resolve current implementations. This is the main anti-lock-in and ERP-evolution mechanism.
