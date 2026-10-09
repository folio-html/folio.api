# Changelog

All notable changes to _folio.api_ are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Startup log warning when `Folio:Password` is empty (every login would return `401`) or still the default `change-me`, so a misconfigured password is visible in the logs instead of surfacing only as a confusing client-side `401`.

### Changed

- **BREAKING:** Replaced static bearer-token auth with a password login that issues an encrypted, HttpOnly session cookie. Clients now `POST /v1/auth/login` with `{ "password": "…" }`, then send the cookie on `/v1/journals` requests; `/v1/auth/logout` and `/v1/auth/session` round out the new auth surface. The `Folio:Token` / `Folio__Token` setting is renamed to `Folio:Password` / `Folio__Password` (compose env: `FOLIO_PASSWORD`). The password is still compared in constant time. Session-cookie keys are persisted under `data/.keys` so sessions survive restarts and redeploys. Cross-origin frontends (when `AllowedOrigins` is set) now require HTTPS, since the cookie becomes `SameSite=None; Secure`.
- `docker-compose.yml.sample` now mounts a sibling _folio.html_ clone's `public/` directory as `wwwroot` instead of a single copied `folio.html` file. A single-file bind mount pins the file's inode, so after a `git pull` the container kept serving the stale frontend until it was recreated.

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
