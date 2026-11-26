#!/usr/bin/env bash
set -euo pipefail

number='(0|[1-9][0-9]*)'
if ! [[ "$TAG" =~ ^v${number}\.${number}\.${number}(-rc\.${number})?$ ]]; then
  echo "Expected vMAJOR.MINOR.PATCH or vMAJOR.MINOR.PATCH-rc.N." >&2
  exit 1
fi

# Drafts can reserve a version before GitHub creates its tag on publication.
available=$(gh api graphql \
  -f owner="${GITHUB_REPOSITORY%/*}" -f repo="${GITHUB_REPOSITORY#*/}" \
  -f tag="$TAG" -f ref="refs/tags/$TAG" \
  -f query='query($owner: String!, $repo: String!, $tag: String!, $ref: String!) {
    repository(owner: $owner, name: $repo) {
      ref(qualifiedName: $ref) { id }
      release(tagName: $tag) { id }
    }
  }' --jq '.data.repository | . != null and .ref == null and .release == null')
if [[ "$available" != true ]]; then
  echo "Release version $TAG is unavailable. Check the repository tags and releases, including drafts." >&2
  exit 1
fi
