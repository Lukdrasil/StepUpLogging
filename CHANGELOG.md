# Changelog

All notable changes to this project will be documented in this file.

## [Unreleased]

### Added
- AlwaysLogRequestSummary option to enable a guaranteed per-request Information-level summary.
- SummarySink to forward IsRequestSummary events to a DI-managed summary logger so summaries are exported independently of the step-up level.
- StepUpLoggingController.EmitRequestSummary API to emit structured request summaries (method, path, status code, elapsedMs, traceId).
- Unit and integration tests for the new summary behavior.

### Fixed
- Avoid duplicate unmanaged Serilog logger instances by centralizing the DI-managed summary logger.

## [1.13.1] - 2026-06-21

### Fixed
- AdditionalSensitiveHeaders are now matched case-insensitively. Previously the comparer was lost when copying the built-in header set, so custom sensitive headers were only redacted when their casing exactly matched the incoming request header - leaking secret values otherwise.

## [1.8.0] - 2026-03-06

### Added
- Release of summary sink and AlwaysLogRequestSummary feature.


*See README.md for configuration examples.*
