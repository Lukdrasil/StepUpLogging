---
applyTo: ' ** / *.cs'
---

# Coding standards and preferences for AI

Coding standards, domain knowledge, and preferences that AI should follow.

## Namespaces

- Use file-scoped namespaces that match the folder structure.

## Immutability

- Prefer immutable types unless mutability is requested.
- Prefer records over classes for immutable types.

## Files Organization

- Define one type per file.

## Record Design

- Define record's properties on the same line with the record declaration.
- For each record `Name`, add a static factory class `NameFactory`.
- Place the factory class in the same file as the record.
- Expose static `Create` method in the factory class for instantiation.
- Place argument validation in the `Create` method.
- Never use record's constructor when there is a factory method.
- Use immutable collections in records unless requested otherwise.
- Use `ImmutableList<T>` in records whenever possible.
- Define record behavior in extension methods in other static classes.

## Discriminated Unions Design

- Prefer using records for discriminated unions.
- Derive specific types from a base abstract record.
- Define the entire discriminated union in one file.
- Define one static factories class per discriminated union.
- Expose one static factory method per variant.
- Follow all rules for record design when designing a discriminated union.

## Test Driven Development (TDD)

- Write tests first (Red → Green → Refactor) in small steps.
- Tests define the public behavioral contract, not the implementation.
- Assert only inputs, outputs, thrown exceptions, and observable side effects (for example, changes via public interfaces, published messages).
- Never test internal details (private methods, internal state, call order, concrete data structures); refactors must not break tests.
- Test structure: AAA (Arrange–Act–Assert) or Given–When–Then. Naming: `Should_<Expected>_When_<Context>`.
- Isolation and clarity: each test covers one behavioral rule; multiple asserts are okay only when checking one logical thing.
- Test doubles and boundaries: mock only at boundaries (I/O, DB, message bus, HTTP). Don’t mock pure functions or value objects.
- Prefer fakes/stubs over strict mocks; avoid verifying call order unless it’s part of the contract.
- Determinism: stable time (clock abstraction), fixed RNG seed, no network calls; tests should run in parallel.
- Edge cases: null/empty, extreme values, duplicates, missing entities, authorization, timeouts/cancellation (async).
- Test data: minimize fixtures; use builders/factories and prefer immutable records per these rules.
- Coverage: favor meaningful behavior and error-path coverage over a percentage metric.

### AI Workflow in TDD

- Before implementation, the AI presents a “contract” of the function/feature for approval:
- One-sentence purpose.
- Inputs (types, domain rules/validation), outputs, exceptions, side effects, invariants.
- List of edge cases and non-goals (what we explicitly won’t handle).
- The AI proposes candidate tests (happy path + 2–3 key edge cases) and requests user approval.
- Don’t change behavior implementation without explicit approval; preparing test skeletons for review is OK.
- After approval, the AI:
- Writes the approved tests and runs them (expected red),
- Implements the minimal code to make them green,
- Performs a safe refactor with tests on.
