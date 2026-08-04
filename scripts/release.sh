#!/usr/bin/env bash
# Validate and publish an exact GAM release tag. This script never commits or pushes main.

set -euo pipefail

usage() {
  echo "Usage: ./scripts/release.sh vX.Y.Z [--push]" >&2
  exit 2
}

VERSION=${1:-}
PUSH=${2:-}
[[ $# -le 2 ]] || usage
[[ "$VERSION" =~ ^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ ]] || usage
[[ -z "$PUSH" || "$PUSH" == "--push" ]] || usage

SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
REPO_ROOT=$(cd -- "$SCRIPT_DIR/.." && pwd)

BRANCH=$(git -C "$REPO_ROOT" branch --show-current)
[[ "$BRANCH" == "main" ]] || {
  echo "Release requires main; current branch is '$BRANCH'." >&2
  exit 1
}

[[ -z "$(git -C "$REPO_ROOT" status --porcelain)" ]] || {
  echo "The worktree must be clean. This script will not stage or commit changes." >&2
  exit 1
}

git -C "$REPO_ROOT" fetch origin main
HEAD_SHA=$(git -C "$REPO_ROOT" rev-parse HEAD)
ORIGIN_MAIN_SHA=$(git -C "$REPO_ROOT" rev-parse origin/main)
[[ "$HEAD_SHA" == "$ORIGIN_MAIN_SHA" ]] || {
  echo "HEAD must exactly match origin/main." >&2
  exit 1
}

NORMALIZED_VERSION=${VERSION#v}
DECLARED_VERSION=$(sed -n 's:.*<Version>\([^<]*\)</Version>.*:\1:p' "$REPO_ROOT/Directory.Build.props" | head -n 1)
[[ "$DECLARED_VERSION" == "$NORMALIZED_VERSION" ]] || {
  echo "Directory.Build.props declares '$DECLARED_VERSION', not '$NORMALIZED_VERSION'." >&2
  exit 1
}

[[ -f "$REPO_ROOT/docs/releases/$VERSION.md" ]] || {
  echo "Release notes are missing: docs/releases/$VERSION.md" >&2
  exit 1
}

! git -C "$REPO_ROOT" rev-parse --quiet --verify "refs/tags/$VERSION" >/dev/null || {
  echo "Local tag $VERSION already exists." >&2
  exit 1
}
set +e
git -C "$REPO_ROOT" ls-remote --exit-code --tags origin "refs/tags/$VERSION" >/dev/null 2>&1
REMOTE_TAG_EXIT=$?
set -e
if [[ $REMOTE_TAG_EXIT -eq 0 ]]; then
  echo "Remote tag $VERSION already exists." >&2
  exit 1
elif [[ $REMOTE_TAG_EXIT -ne 2 ]]; then
  echo "Could not verify the remote tag state (git exit code $REMOTE_TAG_EXIT)." >&2
  exit 1
fi

if [[ "$PUSH" != "--push" ]]; then
  echo "Release preflight passed for $VERSION at $HEAD_SHA."
  echo "Re-run with --push to create and push the annotated tag."
  exit 0
fi

git -C "$REPO_ROOT" tag -a "$VERSION" -m "GAM $VERSION"
git -C "$REPO_ROOT" push origin "refs/tags/$VERSION"
echo "Pushed annotated tag $VERSION. The Release workflow is now responsible for publication."
