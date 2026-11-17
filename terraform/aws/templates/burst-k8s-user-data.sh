#!/bin/bash
# First boot of a Kubernetes burst worker: the k3s counterpart of burst-swarm-user-data.sh
# (docs/burst.md §7). Contains no secret: the k3s join token is read from SSM through the
# instance role.
set -euo pipefail

# Same k3s version as ansible/roles/k3s/defaults/main.yml: change them together
K3S_VERSION="v1.37.2+k3s1"
AWS_CLI_VERSION="2.40.3"
JOIN_TOKEN_PARAMETER="/hba/burst/k3s-token"
REGION="eu-central-1"
SERVER_URL="https://10.0.20.8:6443" # k8s-ctl, not the cluster VIP (that one serves the app)

IMDS=http://169.254.169.254/latest
IMDS_TOKEN=$(curl -fsS -X PUT "$IMDS/api/token" -H "X-aws-ec2-metadata-token-ttl-seconds: 300")
imds() { curl -fsS -H "X-aws-ec2-metadata-token: $IMDS_TOKEN" "$IMDS/meta-data/$1"; }
INSTANCE_ID=$(imds instance-id)
PRIVATE_IP=$(imds local-ipv4)

# Before k3s starts: Kubernetes names the node after the hostname. burst-* makes the worker use
# S3 (HBA_TIER=auto, through spec.nodeName), and the instance ID tells the burst controller
# which instance a node is
hostnamectl set-hostname "burst-$INSTANCE_ID"

# The lock timeout waits for Ubuntu's own apt jobs at first boot instead of failing
export DEBIAN_FRONTEND=noninteractive
apt-get -o DPkg::Lock::Timeout=120 update
apt-get -o DPkg::Lock::Timeout=120 install -y unzip

# AWS's own installer, not the snap: the Snap Store hung on every launch on 2026-11-16 (docs/burst.md §7)
curl -fsSL --retry 5 --retry-all-errors \
  "https://awscli.amazonaws.com/awscli-exe-linux-x86_64-$AWS_CLI_VERSION.zip" -o /tmp/awscliv2.zip
unzip -q /tmp/awscliv2.zip -d /tmp
/tmp/aws/install
rm -rf /tmp/awscliv2.zip /tmp/aws

TOKEN=$(aws ssm get-parameter --name "$JOIN_TOKEN_PARAMETER" --with-decryption \
  --query Parameter.Value --output text --region "$REGION")

# The agent's settings, as the k3s role writes them on-prem (only root can read the token).
# No flannel MTU: as on Swarm's overlay, TCP between a burst pod and an on-prem pod settles on
# the smaller side, and the Tailscale hop splits larger packets (docs/network.md §3)
mkdir -p /etc/rancher/k3s
(
  umask 077
  cat > /etc/rancher/k3s/config.yaml <<EOF
server: "$SERVER_URL"
token: "$TOKEN"
node-ip: "$PRIVATE_IP"
EOF
)
unset TOKEN

# The installer starts the agent, which joins k8s-ctl through the AWS gateway and ts-gateway
curl -fsSL --retry 5 --retry-all-errors https://get.k3s.io -o /tmp/k3s-install.sh
INSTALL_K3S_VERSION="$K3S_VERSION" INSTALL_K3S_EXEC="agent" sh /tmp/k3s-install.sh
rm -f /tmp/k3s-install.sh
