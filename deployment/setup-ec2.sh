#!/bin/bash
# Run this ONCE on a fresh EC2 instance (Ubuntu 22.04/24.04).
# Usage: bash setup-ec2.sh
set -euo pipefail

INSTALL_DIR=/opt/paperbot
SERVICE_NAME=paperbot
SERVICE_USER=paperbot

echo "==> Installing .NET 8 ASP.NET Core Runtime"
apt-get update -qq
apt-get install -y aspnetcore-runtime-8.0

echo "==> Creating user and directories"
id "$SERVICE_USER" &>/dev/null || useradd --system --no-create-home --shell /usr/sbin/nologin "$SERVICE_USER"
mkdir -p "$INSTALL_DIR"/{data,output}
chown -R "$SERVICE_USER:$SERVICE_USER" "$INSTALL_DIR"

echo "==> Installing systemd service"
cp "$(dirname "$0")/paperbot.service" /etc/systemd/system/paperbot.service
systemctl daemon-reload
systemctl enable "$SERVICE_NAME"

echo ""
echo "Setup complete. Deploy the app with deploy.sh, then:"
echo "  sudo systemctl start $SERVICE_NAME"
echo "  sudo journalctl -u $SERVICE_NAME -f"
