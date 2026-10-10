# FlowPlane frontend development

Frontend tooling runs in the development image; no host Node.js installation is required.

Build the image with the WSL user's numeric identity so files written through the bind mount remain editable by that user:

```sh
docker build \
  --file src/frontend/Dockerfile \
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

Replace `typecheck` with `test` or `build` as needed. To start Vite, omit the command and publish port `5173` temporarily; A-04 will place Nginx in front of Vite for the normal browser path.
