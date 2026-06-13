#!/bin/bash
# Build and deploy to EC2. Restarts the service when done.
# Usage: ./deployment/deploy.sh -h <ec2-host> -i <path-to-key.pem> [-u <ssh-user>] [-s <service>] [-d <install-dir>] [-p <port>] [-n (skip build)]
#
# Examples:
#   ./deployment/deploy.sh -h 100.112.204.21 -i ~/.ssh/paperbot-key.pem               # ETH bot (defaults)
#   ./deployment/deploy.sh -h 100.112.204.21 -i ~/.ssh/paperbot-key.pem -s paperbot-btc -d /opt/paperbot-btc -p 5001 -n
set -euo pipefail

INSTALL_DIR=/opt/paperbot
SERVICE_NAME=paperbot
SERVICE_USER=        # OS user owning the install dir; defaults to SERVICE_NAME if not set
PORT=5000
SSH_USER=ubuntu
EC2_HOST=
SSH_KEY=
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

SKIP_BUILD=0
usage() { echo "Usage: $0 -h <ec2-host> -i <key.pem> [-u <ssh-user>] [-s <service>] [-U <service-user>] [-d <install-dir>] [-p <port>] [-n (skip build)]"; exit 1; }

while getopts "h:i:u:s:U:d:p:n" opt; do
  case $opt in
    h) EC2_HOST="$OPTARG" ;;
    i) SSH_KEY="$OPTARG" ;;
    u) SSH_USER="$OPTARG" ;;
    s) SERVICE_NAME="$OPTARG" ;;
    U) SERVICE_USER="$OPTARG" ;;
    d) INSTALL_DIR="$OPTARG" ;;
    p) PORT="$OPTARG" ;;
    n) SKIP_BUILD=1 ;;
    *) usage ;;
  esac
done
SERVICE_USER="${SERVICE_USER:-$SERVICE_NAME}"
[[ -z "$EC2_HOST" || -z "$SSH_KEY" ]] && usage

SSH="ssh -i $SSH_KEY -o StrictHostKeyChecking=accept-new $SSH_USER@$EC2_HOST"
SCP="scp -i $SSH_KEY -o StrictHostKeyChecking=accept-new"

if [[ $SKIP_BUILD -eq 0 ]]; then
  echo "==> Building (Release, framework-dependent)"
  rm -rf "$REPO_ROOT/publish"
  cd "$REPO_ROOT"
  dotnet publish PaperTradingBot.csproj \
    -c Release \
    -o "$REPO_ROOT/publish" \
    --nologo -q
else
  echo "==> Skipping build (using existing publish/)"
fi

echo "==> Packaging (excluding database and instance-specific config)"
TARBALL=$(mktemp /tmp/paperbot-XXXXXX.tar.gz)
tar -czf "$TARBALL" \
  --exclude='*.db' \
  --exclude='*.db-shm' \
  --exclude='*.db-wal' \
  --exclude='appsettings.json' \
  --exclude='data/live_settings.json' \
  --exclude='data/bandit_state.json' \
  -C "$REPO_ROOT/publish" .

echo "==> Uploading to $EC2_HOST"
$SCP "$TARBALL" "$SSH_USER@$EC2_HOST:/tmp/paperbot-deploy.tar.gz"
rm -f "$TARBALL"

echo "==> Deploying on EC2 (service=$SERVICE_NAME dir=$INSTALL_DIR)"
$SSH bash -s <<REMOTE
  set -euo pipefail
  echo "  Stopping service..."
  sudo systemctl stop $SERVICE_NAME 2>/dev/null || true

  echo "  Extracting..."
  sudo tar -xzf /tmp/paperbot-deploy.tar.gz -C $INSTALL_DIR
  sudo chown -R $SERVICE_USER:$SERVICE_USER $INSTALL_DIR
  # Static assets are served directly; Windows-packed dirs can land as 0700 and
  # break static file serving (blank ApexCharts dashboard). Force world read/traverse.
  sudo chmod -R a+rX $INSTALL_DIR/wwwroot
  rm -f /tmp/paperbot-deploy.tar.gz

  echo "  Starting service..."
  sudo systemctl start $SERVICE_NAME
  sleep 2
  sudo systemctl status $SERVICE_NAME --no-pager
REMOTE

echo ""
echo "Done. Dashboard: http://$EC2_HOST:$PORT"
echo "Logs: ssh -i $SSH_KEY $SSH_USER@$EC2_HOST 'sudo journalctl -u $SERVICE_NAME -f'"
