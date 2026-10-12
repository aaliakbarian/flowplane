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
