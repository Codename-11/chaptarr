# Fork Contract

This repository is a maintained deployment fork of [Chaptarr/chaptarr](https://github.com/Chaptarr/chaptarr).

## Branches

- `develop` mirrors `Chaptarr/chaptarr:develop` without local commits.
- `fix/*` and `feature/*` are focused upstream pull-request branches. They are disposable review surfaces and are not deployment targets.
- `deploy` is the operational branch. It merges current upstream `develop` with any temporary local carries that are still required in production.

The fork's default branch is `deploy` so operational builds and GitHub workflow discovery resolve to the deployed source. Upstream pull requests must still target `Chaptarr/chaptarr:develop` from focused branches based on the clean `develop` mirror.

## Current local carries

`deploy` currently carries the `Only This Book` catalog-isolation fix proposed upstream in [Chaptarr/chaptarr#37](https://github.com/Chaptarr/chaptarr/pull/37).

The fix keeps unrelated author titles out of the local catalog when a new author is added with `Only This Book`, while preserving the selected title through Chaptarr's existing manual-add path.

`deploy` also carries the media-specific text lookup fix from `fix/book-lookup-media-types` (`d24f9bd`). The focused branch is ready for upstream review but has not been submitted. It resolves lightweight Goodreads search hits through their canonical work records before filtering by media type, and only returns ebook or audiobook instances backed by matching edition metadata.

## Upstream sync

1. Fetch `Chaptarr/chaptarr:develop` into the `origin` remote.
2. Fast-forward the fork's clean `develop` branch to match upstream exactly.
3. Merge `develop` into `deploy`; do not rebase `deploy`.
4. Resolve conflicts by preserving documented local carries only where upstream has not superseded them.
5. Run the repository's required test/build gates before deploying.
6. Build production images from an exact `deploy` commit and pin the deployment to that commit-derived image tag.

## Carry retirement

When an upstream release contains a carried fix:

1. Compare the merged upstream implementation with the local carry.
2. Accept upstream's implementation unless this contract documents a remaining fork requirement.
3. Remove or revert the local carry from `deploy` so the branch contains only still-required differences.
4. Rebuild, redeploy, and verify the runtime from the resulting `deploy` commit.
5. Delete merged/closed `fix/*` branches after their review history is no longer needed locally.

Do not merge `deploy` back into `develop`, and do not open upstream pull requests from `deploy`.
