#!/usr/bin/env bash
# Regenerate gRPC stubs for all three languages from protos/. One contract, three languages.
#   Go     -> services/engine/gen   (committed)
#   Python -> services/brain/gen     (committed)
#   C#     -> services/ledger/src/Boys.Ledger.Contracts/gen (committed; build-time Grpc.Tools codegen
#            segfaults in the arm64 Docker build, so we pre-generate like the other two languages)
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

echo "== C# (services/ledger/src/Boys.Ledger.Contracts/gen) =="
CS_OUT="$ROOT/services/ledger/src/Boys.Ledger.Contracts/gen"
GRPC_TOOLS_DIR=$(ls -d "$HOME"/.nuget/packages/grpc.tools/*/ 2>/dev/null | sort -V | tail -1)
if [ -z "$GRPC_TOOLS_DIR" ]; then
    echo "  Grpc.Tools not in the NuGet cache — run 'dotnet restore' on a project that references it, then retry." >&2
    exit 1
fi
CS_PLUGIN=$(ls "$GRPC_TOOLS_DIR"tools/macosx_*/grpc_csharp_plugin 2>/dev/null | head -1)
rm -rf "$CS_OUT"; mkdir -p "$CS_OUT"
( cd "$PROTO_DIR" && protoc -I . \
    --csharp_out="$CS_OUT" \
    --grpc_out="$CS_OUT" --plugin=protoc-gen-grpc="$CS_PLUGIN" \
    $PROTOS )

echo "proto codegen complete"
