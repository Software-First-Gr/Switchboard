#!/usr/bin/env bash
# Builds the benchmark app in three variants and runs the runtime-async study:
#   StateMachines  Switchboard and the app with compiler-generated state machines
#   LibraryRA      Switchboard with runtime async, the app without (what a consumer of the package gets)
#   AllRA          both with runtime async (an app that also opts in)
# Stack traces, cold start and a Postgres load test (Testcontainers; needs Docker) run here. The BenchmarkDotNet suites
# run separately:  dotnet run -c Release --project tools/Switchboard.Benchmarks -- --filter '*Study*'
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
out="$root/tools/.study"
project="$root/tools/Switchboard.Benchmarks/Switchboard.Benchmarks.csproj"

build() {
  local name=$1 lib=$2 app=$3
  dotnet build "$project" -c Release --nologo -v quiet -p:UseRuntimeAsync="$lib" -p:AppRuntimeAsync="$app" \
    --artifacts-path "$out/$name" >/dev/null
  echo "$name=$out/$name/bin/Switchboard.Benchmarks/release/Switchboard.Benchmarks"
}

variants="$(build StateMachines false false),$(build LibraryRA true false),$(build AllRA true true)"

"$out/StateMachines/bin/Switchboard.Benchmarks/release/Switchboard.Benchmarks" study \
  --variants "$variants" --out "$out/study-results.json" "$@"
