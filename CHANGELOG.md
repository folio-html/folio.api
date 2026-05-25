# Changelog

All notable changes to _folio.api_ are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.1.0] - 2026-05-25

### Added

- Initial release: a self-hostable API that stores journals as portable `.md` files.
- URL-path API versioning, with the first surface under `/v1`.
- Journal endpoints: list, read, head, write, and delete journals by name.
- Optimistic concurrency via strong `ETag`s and `If-Match` (create-only with `If-Match: *`), returning `412` on conflict.
- Atomic writes (temp file + rename) so a journal is never observed half-written, with optional timestamped backups under `data/.backups/`.
- Static bearer-token authentication on all `/v1` endpoints, compared in constant time.
- `/health` liveness endpoint (ASP.NET Core health checks) that reports unhealthy when the data directory is not writable.
- Optional same-origin hosting of `folio.html` from `wwwroot/`, plus configurable CORS origins for cross-origin use.
- Docker support: multi-stage `Dockerfile` and a Traefik-ready `docker-compose.yml` that persists journals to a mounted volume and serves the frontend same-origin.
