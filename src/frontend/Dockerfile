FROM node:24.21.0-bookworm-slim

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

WORKDIR /workspace/src/frontend

EXPOSE 5173

CMD ["npm", "run", "dev"]
