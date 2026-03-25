# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Comparative analysis of three TCP client implementations (Fael, SiDel, Mb) from Sistec HMI projects, demonstrating their evolutionary progression from basic to production-grade. The repository is primarily Italian-language documentation with a .NET 8 xUnit test suite that proves behavioral differences across all three clients.

## Build & Test Commands

```bash
# Run all tests (from Tests/ directory)
cd Tests
dotnet test --verbosity normal

# Run a single test class
dotnet test --filter "FullyQualifiedName~T01_InstanceNamingTests"

# Run a single test method
dotnet test --filter "FullyQualifiedName~T01_InstanceNamingTests.Fael_HasNoNameProperty"
```

The solution file is `Tests/Tests.sln`, targeting .NET 8 with xUnit 2.7.

## Architecture

**Root-level `_Fael.cs`, `_SiDel.cs`, `_Mb.cs`** are the original production source files (read-only reference). They are NOT part of the test project.

**`Tests/`** is a self-contained .NET test project:

- **`Clients/`** — Standalone test-friendly copies of each TCP client (`FaelTcpClient.cs`, `SiDelTcpClient.cs`, `MbTcpClient.cs`), adapted to compile without the full Sistec solution by using local stubs.
- **`Stubs/`** — Minimal stand-ins for production dependencies (`ConnectResult`, `ReadResult`, `WriteResult`, `ReconnectAgent`, `ReconnectionPolicy`, `Utilities`, `FakeLoggerFactory`, etc.) so the clients compile in isolation.
- **`Helpers/`** — `ReflectionHelper` (access private fields for assertions) and `TcpListenerHelper` (spin up local TCP listeners for integration-style tests).
- **`EvolutionTests/`** — 7 test files (`T01`–`T07`), each covering one evolution area. Every test file runs the same scenario against all three clients, asserting **actual behavior including known bugs** (e.g., SiDel's broken name constructor is asserted as broken, not fixed).

## Key Conventions

- Tests intentionally assert buggy behavior in older clients (Fael, SiDel) to document the evolution. Do not "fix" these assertions.
- The evolution order is: Fael (base) → SiDel (adds features with bugs) → Mb (fixes bugs, adds robustness).
- Documentation files (`Confronto_TcpClient.md`, `modifiche_Mb_rispetto_a_SiDel.md`, `evaluation.md`) are in Italian.

## Markdown

table in .md files must be:

| a | b | c | d |
| --- | --- | --- | --- |

not:

| a | b | c | d |
|---|---|---|---|
