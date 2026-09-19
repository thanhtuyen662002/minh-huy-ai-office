# ADR-0001: Modular Monolith + Worker Services First

Status: Accepted

Start with one product monorepo, a modular ASP.NET Core API, separate workers and supporting infrastructure. Do not prematurely split every bounded context into a microservice. Split services only when load/security/deployment boundaries justify it.
