FROM node:24.21.0-bookworm-slim AS frontend-tooling

ARG USER_UID=1000
ARG USER_GID=1000

RUN groupmod --gid "$USER_GID" node \
    && usermod --uid "$USER_UID" --gid "$USER_GID" node \
    && chown --recursive node:node /home/node \
    && install --directory --owner=node --group=node /workspace

WORKDIR /workspace

COPY --chown=node:node src/frontend/package.json src/frontend/package-lock.json ./

USER node

RUN npm ci

ENV PATH="/workspace/node_modules/.bin:${PATH}"

FROM frontend-tooling AS development

WORKDIR /workspace/src/frontend

EXPOSE 5173

CMD ["npm", "run", "dev"]

FROM frontend-tooling AS build

ARG VITE_FLOWPLANE_OIDC_AUTHORITY=http://127.0.0.1:8081/realms/flowplane
ARG VITE_FLOWPLANE_OIDC_CLIENT_ID=flowplane-spa

ENV VITE_FLOWPLANE_OIDC_AUTHORITY="$VITE_FLOWPLANE_OIDC_AUTHORITY"
ENV VITE_FLOWPLANE_OIDC_CLIENT_ID="$VITE_FLOWPLANE_OIDC_CLIENT_ID"

COPY --chown=node:node src/frontend/ /workspace/src/frontend/

WORKDIR /workspace/src/frontend

RUN npm run build

FROM nginx:1.30.5-alpine3.24 AS production

COPY deploy/compose/nginx/production.conf /etc/nginx/conf.d/default.conf
COPY --from=build /workspace/src/frontend/dist/ /usr/share/nginx/html/

EXPOSE 80
