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

## [1.8.0] - 2026-03-06

### Added
- Release of summary sink and AlwaysLogRequestSummary feature.


*See README.md for configuration examples.*
