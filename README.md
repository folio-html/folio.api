# folio.api

A tiny, self-hostable backend for [_folio.html_](https://github.com/DanielGilbert/folio).

It puts an API between the journal frontend and the markdown file on disk:

```
folio.html  <->  folio.api  <->  journal.md
```

The point is portability. Each journal stays a plain `.md` file you can read,
edit, or back up with any tool — _folio.api_ is just a thin sync layer over it,
and you can throw it away without losing anything.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download) (LTS)

## Running

```bash
dotnet run
```

The API listens on `http://localhost:5080` by default (see
`Properties/launchSettings.json`). Journals are stored as `{name}.md` under the
`data/` directory.

### Serving the frontend

Drop `folio.html` into `wwwroot/` and it is served at `/` from the same origin
as the API. _folio.html_ auto-detects its endpoint from `location.origin`, so no
client configuration is needed for the bundled case.

```bash
cp ../folio/public/folio.html wwwroot/folio.html
```

## Docker

A multi-stage `Dockerfile` and a `docker-compose.yml.sample` are included. Copy
the sample, drop your `folio.html` next to it, and bring it up:

```bash
cp docker-compose.yml.sample docker-compose.yml
cp ../folio.html/public/folio.html ./folio.html
echo "FOLIO_TOKEN=$(openssl rand -hex 32)" > .env   # token is required
docker compose up -d --build
```

The sample compose:

- persists journals + backups to `./data` (mounted at `/data`);
- mounts `folio.html` into `/app/wwwroot/folio.html` so the frontend is served
  same-origin (no CORS needed);
- publishes port `8080`. Put a reverse proxy (Traefik, nginx, Caddy, …) in front
  for TLS and routing — that part is deployment-specific and left out of the
  sample.

The container runs as the default user so the bind-mounted `/data` stays
writable; `/health` is available for external monitoring.

### Updating

The API is compiled into the image, so pulling new source is not enough — the
container keeps the old build until you rebuild it:

```bash
git pull && docker compose up -d --build
```

(The mounted `folio.html` is the exception — it is served live off disk, so
frontend updates apply without a rebuild.) For an auto-deploying server, run the
above from cron only when there are new commits:

```bash
git fetch --quiet
[ "$(git rev-parse @)" != "$(git rev-parse '@{u}')" ] && git pull --quiet && docker compose up -d --build
```

## Configuration

Settings live in `appsettings.json` under the `Folio` section. Anything can be
overridden by environment variables using the `Folio__Key` convention — keep the
token out of the file in real deployments.

| Key | Env var | Default | Meaning |
|-----|---------|---------|---------|
| `Token` | `Folio__Token` | `change-me` | Static bearer token required on every API call |
| `DataDir` | `Folio__DataDir` | `data` | Directory holding the `.md` journals |
| `Backups` | `Folio__Backups` | `true` | Copy the previous version into `data/.backups/` before each write/delete |
| `AllowedOrigins` | — | `[]` | CORS origins; leave empty for same-origin (bundled) deployments |

## Authentication

Every `/v1` request must send the token:

```
Authorization: Bearer <token>
```

Missing or wrong token → `401`. The comparison is constant-time.

## API

The API is versioned by URL path. Today everything lives under `/v1`; a breaking
change would introduce `/v2` while `/v1` keeps serving existing clients.

| Method | Path | Notes |
|--------|------|-------|
| `GET` | `/health` | Liveness; `200 Healthy` while the data directory is writable, else `503`. No auth. |
| `GET` | `/v1/journals` | List journals: `[{ name, size, modified, etag }]` |
| `GET` | `/v1/journals/{name}` | Journal markdown; `ETag` header. `404` if absent. |
| `HEAD` | `/v1/journals/{name}` | `ETag` only — cheap conflict poll. |
| `PUT` | `/v1/journals/{name}` | Write markdown body. See concurrency below. |
| `DELETE` | `/v1/journals/{name}` | Remove a journal. |

`{name}` must match `^[A-Za-z0-9._-]{1,64}$`.

### Optimistic concurrency

Reads return a strong `ETag` (a SHA-256 of the file bytes). To avoid clobbering a
journal that changed since you last read it, echo that value back on write:

- `If-Match: "<etag>"` — write only if the server's current version matches,
  otherwise `412 Precondition Failed`.
- `If-Match: *` — create only; `412` if the journal already exists.
- No `If-Match` — unconditional write (last-writer-wins).

A `412` is the cue to reload the server's copy and reconcile rather than
overwrite.

### Durability

Writes are atomic: the new content is written to a temp file and then renamed
over the target, so a journal is never observed half-written. With `Backups`
enabled, the prior version is copied into `data/.backups/` first.

## License

[GNU Affero General Public License v3.0](LICENSE.md) © 2026 Daniel Gilbert
