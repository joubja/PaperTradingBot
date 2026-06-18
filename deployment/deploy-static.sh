#!/bin/bash
# Deploy the STATIC Reality Check site (site/) to nginx on EC2.
# The public site is now plain HTML/CSS/JS served by nginx from /var/www/hbb —
# the Blazor app is no longer used to serve it. Regenerates the bundled data from
# wwwroot/ (source of truth), then uploads.
#
# Usage: ./deployment/deploy-static.sh -h <ec2-host> -i <key.pem>
set -euo pipefail

EC2_HOST=; SSH_KEY=; SSH_USER=ubuntu
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"; REPO="$(cd "$SCRIPT_DIR/.." && pwd)"
while getopts "h:i:u:" o; do case $o in h) EC2_HOST=$OPTARG;; i) SSH_KEY=$OPTARG;; u) SSH_USER=$OPTARG;; *) exit 1;; esac; done
[[ -z "$EC2_HOST" || -z "$SSH_KEY" ]] && { echo "Usage: $0 -h <host> -i <key.pem>"; exit 1; }

echo "==> Regenerating bundled data from wwwroot/"
mkdir -p "$REPO/site/data" "$REPO/site/snapshots"
( cd "$REPO/wwwroot/reality-check"; first=1; { printf '['; for f in *.json; do [[ $first -eq 1 ]] && first=0 || printf ','; cat "$f"; done; printf ']'; } ) > "$REPO/site/data/reality.json"
cp "$REPO"/wwwroot/snapshots/*.json "$REPO/site/snapshots/"
cp "$REPO"/wwwroot/favicon.svg "$REPO"/wwwroot/robots.txt "$REPO"/wwwroot/sitemap.xml "$REPO/site/"

echo "==> Uploading site/ -> /var/www/hbb on $EC2_HOST"
tar -czf - -C "$REPO/site" . | ssh -i "$SSH_KEY" -o StrictHostKeyChecking=accept-new "$SSH_USER@$EC2_HOST" \
  'sudo rm -rf /var/www/hbb && sudo mkdir -p /var/www/hbb && sudo tar -xzf - -C /var/www/hbb && sudo chown -R www-data:www-data /var/www/hbb && sudo chmod -R a+rX /var/www/hbb'

echo "Done. https://hodlbeatsbots.com/"
