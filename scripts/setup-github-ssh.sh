#!/usr/bin/env bash
#
# One-time GitHub SSH setup for the db-test repository.
#
# Run this after adding the public key to your GitHub account (see step 1 below).
# It is idempotent: run it again any time to re-apply the configuration.
#
# Step 1 (browser, once): add the public key to GitHub
#   https://github.com/settings/ssh/new
#   Title:      db-test (id_ed25519_github)
#   Key:        <the contents of ~/.ssh/id_ed25519_github.pub>
#   The account you are logged into must be dbaitest - the owner of dbaitest/db-test.
#
# Step 2 (this script): point git at the key and verify the connection.
#
set -euo pipefail

KEY_FILE="$HOME/.ssh/id_ed25519_github"
SSH_CONFIG="$HOME/.ssh/config"
REPO_URL="git@github.com:dbaitest/db-test.git"

if [ ! -f "$KEY_FILE" ]; then
    echo "ERROR: $KEY_FILE not found." >&2
    echo "Generate it with:" >&2
    echo "  ssh-keygen -t ed25519 -C \"dbaitest@db-test\" -f \"$KEY_FILE\" -N \"\"" >&2
    exit 1
fi

# Add a github.com block to the SSH config, if it is not already there.
if ! grep -q "^Host github\.com$" "$SSH_CONFIG" 2>/dev/null; then
    cat >> "$SSH_CONFIG" <<EOF

# GitHub over SSH. A dedicated key, separate from the internal hosts, so it can be
# revoked on its own without touching access to anything else.
Host github.com
    User git
    IdentityFile $KEY_FILE
    IdentitiesOnly yes
EOF
    echo "Added a github.com block to $SSH_CONFIG"
else
    echo "$SSH_CONFIG already configures github.com"
fi

# Point this repository at SSH.
if [ -d .git ]; then
    git remote set-url origin "$REPO_URL"
    echo "Remote origin set to $REPO_URL"
fi

# Verify. GitHub answers a successful SSH auth with a greeting on stderr and a
# non-zero exit, so the exit code is not the signal - the greeting is.
echo
echo "Checking the connection to github.com..."
output=$(ssh -o BatchMode=yes -o StrictHostKeyChecking=accept-new -T git@github.com 2>&1 || true)

if echo "$output" | grep -q "successfully authenticated"; then
    echo
    echo "OK: $output"
    echo
    if [ -d .git ]; then
        git push -u origin HEAD 2>&1 | tail -5
    fi
    echo
    echo "Setup complete. Future pushes need no token and no browser step."
else
    echo "$output" >&2
    echo
    echo "Still denied. Check that:" >&2
    echo "  1. the public key is listed at https://github.com/settings/keys" >&2
    echo "  2. it was added to the dbaitest account, not another one" >&2
    echo "  3. the key below matches the one you uploaded:" >&2
    echo
    cat "$KEY_FILE.pub" >&2
    exit 1
fi
