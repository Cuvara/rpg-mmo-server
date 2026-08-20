# Builds the Nakama Go runtime plugin (nakama.so) from backend/nakama.
#
# IMPORTANT: the build context must be `backend/` — NOT `backend/nakama/` and
# NOT the repo root. The nakama module has `replace github.com/duycuong/rpg-mmo/shared
# => ../shared`, so both `nakama/` and `shared/` must be visible to the build.
#
# Build the runnable image (plugin baked in):
#   docker build -f backend/deploy/nakama-plugin.Dockerfile \
#     -t rpg-mmo/nakama:3.40.0 backend/
#
# Export just nakama.so into deploy/modules/ (what docker-compose mounts):
#   DOCKER_BUILDKIT=1 docker build -f backend/deploy/nakama-plugin.Dockerfile \
#     --target export --output type=local,dest=backend/deploy/modules backend/
#
# The pluginbuilder tag MUST equal the nakama server tag in docker-compose.yml.
# Go plugins are ABI-locked to the exact Go toolchain + nakama-common version
# used by the server binary; a mismatch fails at load with
# "plugin was built with a different version of package ...".
ARG NAKAMA_VERSION=3.40.0

FROM heroiclabs/nakama-pluginbuilder:${NAKAMA_VERSION} AS builder

ENV CGO_ENABLED=1
ENV GOOS=linux

WORKDIR /backend

# Copy both modules so the `replace` directive resolves.
COPY shared/ shared/
COPY nakama/ nakama/

WORKDIR /backend/nakama

RUN go mod download
RUN go build --trimpath --buildmode=plugin -o /out/nakama.so .

# --target export --output type=local,dest=... writes nakama.so to the host.
FROM scratch AS export
COPY --from=builder /out/nakama.so /nakama.so

# Default target: nakama server with the plugin baked into the modules dir.
FROM heroiclabs/nakama:${NAKAMA_VERSION} AS runtime
COPY --from=builder /out/nakama.so /nakama/data/modules/nakama.so

# Provenance. Declared AFTER the copy so a rebuild at a new revision does not
# invalidate the layer above it -- the same reasoning as docker/Dockerfile.gateway.
#
# WITHOUT THIS, THE IMAGE LIES ABOUT ITSELF, and convincingly. Every label here is
# inherited from heroiclabs/nakama, including
# `org.opencontainers.image.revision`, so `docker image inspect` reports
# d4d92f93f78bbbe62c7fc50a3f85c772ec121a09 -- which is a real commit, in
# heroiclabs/nakama ("Prepare 3.40.0 release. (#2527)"), and says nothing at all
# about the plugin baked in on the line above.
#
# That is worse than an unstamped image. An unstamped one reports `unknown` and
# any check skips it; this one reports a plausible 40-char sha that resolves
# nowhere in this repository, which reads as "built from a branch since deleted"
# and sends you looking for a commit that never existed here.
ARG GIT_REVISION=unknown
LABEL org.opencontainers.image.revision="${GIT_REVISION}" \
      org.opencontainers.image.title="rpg-mmo nakama (plugin baked in)" \
      org.opencontainers.image.source="https://github.com/cuvara/rpg-mmo-server" 
