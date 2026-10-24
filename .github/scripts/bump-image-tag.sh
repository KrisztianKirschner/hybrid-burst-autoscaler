#!/usr/bin/env bash
# Points a stack file at the sha tag of a freshly pushed image and opens (or updates) the PR
# that deploys it. Called by .github/workflows/bump-image.yml after an image push on main.
#
# usage: bump-image-tag.sh <image> <stack-file>
#   image       name without registry, e.g. hba-autoscaler
#   stack-file  the file that pins it, e.g. stacks/swarm/autoscaler.yml
#
# Environment:
#   GITHUB_SHA  the commit the image was built from (set by Actions)
#   GH_TOKEN    token for gh (pull-requests: write); not needed with DRY_RUN
#   DRY_RUN=1   only edit the file and print the diff: no commit, push or PR (for local tests)
set -euo pipefail

if [ "$#" -ne 2 ]; then
  sed -n '2,12p' "$0" >&2
  exit 2
fi

name=$1
file=$2
image="ghcr.io/krisztiankirschner/$name"
branch="bump/$name"
sha=${GITHUB_SHA:?GITHUB_SHA is not set}
# The tag the image job pushed: docker/metadata-action with type=sha,format=short (7 chars)
tag="sha-${sha:0:7}"

sed -i -E "s#(image: ${image}):sha-[0-9a-f]+#\1:${tag}#" "$file"
if git diff --quiet -- "$file"; then
  echo "$file already uses $image:$tag"
  exit 0
fi
git --no-pager diff -- "$file"

if [ "${DRY_RUN:-0}" = "1" ]; then
  echo "DRY_RUN: not committing, pushing or opening a PR"
  exit 0
fi

git config user.name "github-actions[bot]"
git config user.email "41898282+github-actions[bot]@users.noreply.github.com"
git switch -c "$branch"
git commit -m "chore: bump $name to $tag" -- "$file"
# Force: the branch always holds exactly one bump on top of the latest main
git push --force origin "$branch"

title="chore: bump $name to $tag"
body="Points \`$file\` at \`$image:$tag\`, built from $GITHUB_SHA. Merging deploys it.

Opened by the image workflow. PRs opened with the workflow token start no workflows, so this PR shows no checks."

if [ -n "$(gh pr list --head "$branch" --state open --json number --jq '.[].number')" ]; then
  gh pr edit "$branch" --title "$title" --body "$body"
else
  gh pr create --base main --head "$branch" --title "$title" --body "$body"
fi
