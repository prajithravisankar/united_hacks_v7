#!/usr/bin/env bash
# Regenerate gRPC stubs for all three languages from protos/. One contract, three languages.
#   Go     -> services/engine/gen   (committed)
#   Python -> services/brain/gen     (committed)
#   C#     -> generated at build by Grpc.Tools into obj/ (not committed)
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROTO_DIR="$ROOT/protos"
PROTOS=$(cd "$PROTO_DIR" && find boys -name '*.proto' | sort)

echo "== Go (services/engine/gen) =="
export PATH="$PATH:$(go env GOPATH)/bin"
GO_OUT="$ROOT/services/engine/gen"
rm -rf "$GO_OUT"; mkdir -p "$GO_OUT"
( cd "$PROTO_DIR" && protoc -I . \
    --go_out="$GO_OUT" --go_opt=paths=source_relative \
    --go-grpc_out="$GO_OUT" --go-grpc_opt=paths=source_relative \
    $PROTOS )

echo "== Python (services/brain/gen) =="
PY_OUT="$ROOT/services/brain/gen"
rm -rf "$PY_OUT"; mkdir -p "$PY_OUT"
( cd "$ROOT/services/brain" && uv run --quiet python -m grpc_tools.protoc -I "$PROTO_DIR" \
    --python_out=gen --grpc_python_out=gen --pyi_out=gen \
    $PROTOS )

echo "== C# (Grpc.Tools, at build) =="
dotnet build "$ROOT/services/ledger/src/Boys.Ledger.Contracts/Boys.Ledger.Contracts.csproj" --nologo -v q

echo "proto codegen complete"
