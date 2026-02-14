#!/bin/bash
# Create a new release for GAM
# Usage: ./release.sh [major|minor|patch] [message]

set -e

# Colors
RED='\033[0;31m'
GREEN='\033[0;32m'
CYAN='\033[0;36m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

# Functions
error() {
    echo -e "${RED}Error: $1${NC}" >&2
    exit 1
}

success() {
    echo -e "${GREEN}$1${NC}"
}

info() {
    echo -e "${CYAN}$1${NC}"
}

warning() {
    echo -e "${YELLOW}$1${NC}"
}

# Check if we're in a git repository
if [ ! -d .git ]; then
    error "This script must be run from the root of the GAM repository"
fi

# Parse arguments
RELEASE_TYPE=${1:-patch}
MESSAGE=${2:-""}

# Validate release type
if [[ ! "$RELEASE_TYPE" =~ ^(major|minor|patch)$ ]]; then
    error "Invalid release type. Must be: major, minor, or patch"
fi

# Check for uncommitted changes
if [ -n "$(git status --porcelain)" ]; then
    warning "You have uncommitted changes:"
    git status --short
    read -p "Do you want to commit these changes? (y/n) " -n 1 -r
    echo
    if [[ ! $REPLY =~ ^[Yy]$ ]]; then
        info "Aborting release"
        exit 0
    fi
fi

# Get the latest tag
LATEST_TAG=$(git describe --tags --abbrev=0 2>/dev/null || echo "v0.0.0")
info "Latest tag: $LATEST_TAG"

# Calculate next version
VERSION=${LATEST_TAG#v}
IFS='.' read -r -a VERSION_PARTS <<< "$VERSION"
MAJOR=${VERSION_PARTS[0]:-0}
MINOR=${VERSION_PARTS[1]:-0}
PATCH=${VERSION_PARTS[2]:-0}

case $RELEASE_TYPE in
    major)
        MAJOR=$((MAJOR + 1))
        MINOR=0
        PATCH=0
        ;;
    minor)
        MINOR=$((MINOR + 1))
        PATCH=0
        ;;
    patch)
        PATCH=$((PATCH + 1))
        ;;
esac

NEW_VERSION="v${MAJOR}.${MINOR}.${PATCH}"
info "Next version will be: $NEW_VERSION"

# Build commit message
if [ -n "$MESSAGE" ]; then
    COMMIT_MSG="$MESSAGE

[release:$RELEASE_TYPE]"
else
    COMMIT_MSG="Release $NEW_VERSION

[release:$RELEASE_TYPE]"
fi

# Commit any changes
if [ -n "$(git status --porcelain)" ]; then
    info "Committing changes..."
    git add -A
    git commit -m "$COMMIT_MSG"
    success "Changes committed"
else
    # Create empty commit to trigger release
    info "Creating release commit..."
    git commit --allow-empty -m "$COMMIT_MSG"
fi

# Create tag for release workflow
if git rev-parse -q --verify "refs/tags/$NEW_VERSION" >/dev/null; then
    error "Tag $NEW_VERSION already exists. Aborting to avoid duplicate release."
fi

git tag "$NEW_VERSION"

# Push to trigger the release workflow
info "Pushing to GitHub..."
git push origin main
git push origin "$NEW_VERSION"

success "Release process started!"
info "Check the Actions tab on GitHub for build progress:"
info "https://github.com/RiRi-380/GAM/actions"
echo
info "Once the build completes, the release will be available at:"
info "https://github.com/RiRi-380/GAM/releases/tag/$NEW_VERSION"
