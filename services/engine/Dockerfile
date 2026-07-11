# Engine service — Go real-time replay + WebSocket streaming. Static binary, ARM-native.
FROM golang:1.25-alpine AS build
WORKDIR /src
COPY go.mod go.sum ./
RUN go mod download
COPY . .
RUN CGO_ENABLED=0 go build -trimpath -o /engine ./cmd/engine

FROM alpine:3.20 AS runtime
# wget (busybox, already present) is used by the compose healthcheck to probe /health/ready.
RUN adduser -D -u 10001 engine
COPY --from=build /engine /engine
USER engine
EXPOSE 8090 50071
ENTRYPOINT ["/engine"]
