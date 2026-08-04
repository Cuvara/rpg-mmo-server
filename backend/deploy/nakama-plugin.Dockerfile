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
