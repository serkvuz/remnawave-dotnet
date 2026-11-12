#!/usr/bin/env bash
# Regenerates the Remnawave C# client from the OpenAPI document.
#
#   scripts/generate.sh             # regenerate from the committed spec
#   scripts/generate.sh --download  # fetch the latest spec from docs.rw first
set -euo pipefail
cd "$(dirname "$0")/.."

SPEC_URL="${REMNAWAVE_SPEC_URL:-https://cdn.docs.rw/docs/openapi.json}"
RAW=spec/remnawave-openapi.json
PROCESSED=spec/remnawave-openapi.processed.json
OUT=src/Remnawave.Client/Generated

if [[ "${1:-}" == "--download" ]]; then
  echo "Downloading $SPEC_URL"
  curl -fsSL "$SPEC_URL" -o "$RAW.tmp"
  # Normalise formatting so diffs are reviewable.
  dotnet run scripts/FormatJson.cs -- "$RAW.tmp" "$RAW"
  rm -f "$RAW.tmp"
fi

VERSION=$(grep -m1 -oP '"version"\s*:\s*"\K[^"]+' "$RAW")
echo "Remnawave API version: $VERSION"
echo "$VERSION" > spec/VERSION

dotnet tool restore >/dev/null
dotnet run scripts/Preprocess.cs -- "$RAW" "$PROCESSED"

dotnet kiota generate \
  --openapi "$PROCESSED" \
  --language CSharp \
  --class-name RemnawaveApiClient \
  --namespace-name Remnawave.Client \
  --output "$OUT" \
  --structured-mime-types application/json \
  --serializer Microsoft.Kiota.Serialization.Json.JsonSerializationWriterFactory \
  --deserializer Microsoft.Kiota.Serialization.Json.JsonParseNodeFactory \
  --exclude-backward-compatible \
  --clean-output \
  --log-level Warning

dotnet run scripts/GenerateExtensions.cs -- "$OUT/Models" src/Remnawave.Client/RemnawaveResponseExtensions.g.cs

echo "Generated $(find "$OUT" -name '*.cs' | wc -l) files into $OUT"
