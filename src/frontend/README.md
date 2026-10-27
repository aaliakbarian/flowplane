# FlowPlane frontend development

Frontend tooling runs in the development image; no host Node.js installation is required.

Build the image with the WSL user's numeric identity so files written through the bind mount remain editable by that user:

```sh
docker build \
  --file src/frontend/Dockerfile \
  --target development \
  --build-arg USER_UID="$(id -u)" \
  --build-arg USER_GID="$(id -g)" \
  --tag flowplane-frontend-dev:a03 \
  .
```

Run a package script with the WSL source tree bind-mounted. Dependencies remain in the image at `/workspace/node_modules`, and Vite/Vitest cache data remains in container-local `/tmp`, both outside the bind mount:

```sh
docker run --rm \
  --mount type=bind,source="$PWD/src/frontend",target=/workspace/src/frontend \
  flowplane-frontend-dev:a03 \
  npm run typecheck
```

Replace `typecheck` with `test` or `build` as needed. To start Vite, omit the command and publish port `5173` temporarily; the normal Compose path places development Nginx in front of Vite.

The same Dockerfile also has `build` and `production` stages. The build stage runs the Vite production build, and the production stage copies only `dist/` and the static-serving configuration into `nginx:1.30.5-alpine3.24`. Use the documented production profile in `deploy/compose/README.md` for the supported same-origin runtime verification rather than running the final image without its backend dependency.

The production build accepts only public browser configuration through `VITE_FLOWPLANE_OIDC_AUTHORITY` and `VITE_FLOWPLANE_OIDC_CLIENT_ID`. Vite embeds these values in the compiled JavaScript, so passwords, tokens, database configuration, client secrets, and other credentials must never be passed as `VITE_*` build arguments. Changing the public OIDC authority requires rebuilding the static frontend image; the callback URI continues to derive from the browser's current origin.

## Milestone A operator journey

After login, the UI calls authenticated `/api/me` and then the operator-protected
provider list. No administration controls/data are shown before that check succeeds.
An identity-bootstrap network/server failure offers a manual **Retry identity**
action: one new authenticated request, with no automatic retry loop or page reload.
No previous identity or inventory is reused while retrying. A 401 instead clears
the visible session and requires sign-in; successful identity loading still does
not grant operator privileges.
Configure exact issuer/subject operators and SSIS host/port allowlists using the
[Stage 1 backend instructions](../../docs/development/a12-stage1-api.md); login alone
does not grant access. No default operator or endpoint is added by the UI.
Enable the existing hosted-discovery configuration only after approved Gate B
application; see [Stage 2](../../docs/development/a12-stage2-discovery.md).

The four UI areas configure an SSIS provider, display validation/capabilities,
observe a durable refresh, and browse published inventory. Creation does not
automatically validate/connect. Editing and credential replacement use the exact
server ETag; reload the provider after 412/428 rather than blindly retrying writes.
Mandatory encryption is fixed; certificate bypass remains explicit and is rejected
by the backend outside Development. Back up external root material and PostgreSQL
separately before onboarding real provider credentials.

Passwords are uncontrolled write-only fields, cleared when submitted, and never
placed in React state, storage, URLs, logs or screenshots. JavaScript/transport
strings cannot be perfectly zeroized. OIDC keeps its existing session storage.
Additional `flowplane.refresh.v1:` records contain only provider ID, UUIDv7 and the
original strong ETag, scoped to the authenticated subject in this single-issuer app.
The record is saved before POST; unavailable storage prevents submission. After
reload, select the same provider to recover its status. Ambiguous retries reuse
the same key and original ETag. Only a definitive rejection of a newly generated
key's first request in the current mounted session can be explicitly discarded.
A later policy/precondition rejection or a reload does not prove an earlier
request was never accepted: its recovery key is retained. No automatic replacement
of uncertain admissions occurs. Status polling respects Retry-After across phase
changes and does not start while admission is in flight. Explicit
logout clears recovery records; session expiry clears visible data but permits
same-subject recovery after reauthentication. Accepted backend work is independent
of browser polling/navigation. There is no Cancel or Execute control.

Inventory defaults to entry-point candidates; metadata views use bounded Stage 3
PostgreSQL pages, never a native scan. Any disclosure failure removes the page.
Expired/restarted/superseded cursors restart at page one; native metadata/visibility
changes require discovery refresh, not stale fallback. Int64 identifiers remain
decimal strings. Default values and environment values are not displayed.

## Synthetic browser verification

`e2e/provider-journey.spec.ts` uses a synthetic session and intercepts every UI API
request. It is not live OIDC/operator or native-provider acceptance. Run only that
file against a running development or production Nginx origin, using the existing
`mcr.microsoft.com/playwright:v1.64.0-noble` image and the documented container-only
Playwright procedure. Set `FLOWPLANE_BASE_URL` to the origin; the origin must route
anonymous `/api/me` to 401 and `/api/health/ready` to 200. No private `.env` is needed.
Do not enable traces, screenshots, videos or request-body logging for credentials.

The separate existing `e2e/oidc-login.spec.ts` verifies real Authorization Code/S256
PKCE login and requires explicitly configured private test credentials. Do not
substitute synthetic session tests for that real-environment evidence. Full real
operator configuration/discovery/browsing remains pending root/database backup
and separately authorized onboarding. No database migrations or new infrastructure
dependencies are introduced by Stage 4.
