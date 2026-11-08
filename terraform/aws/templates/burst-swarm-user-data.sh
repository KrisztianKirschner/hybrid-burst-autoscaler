#!/bin/bash
# First boot of a burst worker (docs/burst.md §7). Contains no secret:
# the Swarm join token is read from SSM through the instance role.
set -euo pipefail

# Same versions as ansible/roles/docker/defaults/main.yml: change them together
DOCKER_VERSION="5:29.8.1-1~ubuntu.26.04~resolute"
CONTAINERD_VERSION="2.3.6-1~ubuntu.26.04~resolute"
JOIN_TOKEN_PARAMETER="/hba/burst/swarm-join-token"
REGION="eu-central-1"
MANAGER_ADDR="10.0.20.11" # swarm-ctl

IMDS=http://169.254.169.254/latest
IMDS_TOKEN=$(curl -fsS -X PUT "$IMDS/api/token" -H "X-aws-ec2-metadata-token-ttl-seconds: 300")
imds() { curl -fsS -H "X-aws-ec2-metadata-token: $IMDS_TOKEN" "$IMDS/meta-data/$1"; }
INSTANCE_ID=$(imds instance-id)
PRIVATE_IP=$(imds local-ipv4)

# Before Docker starts, which reads the hostname once. burst-* makes the worker use S3
# (HBA_TIER=auto), and the instance ID tells the burst controller which instance a node is
hostnamectl set-hostname "burst-$INSTANCE_ID"

snap wait system seed.loaded
snap install aws-cli --classic

# Same log rotation as the docker role. daemon.json first: the package starts Docker
mkdir -p /etc/docker
cat > /etc/docker/daemon.json <<'EOF'
{
  "log-driver": "json-file",
  "log-opts": {
    "max-size": "10m",
    "max-file": "3"
  }
}
EOF

curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
cat > /etc/apt/sources.list.d/docker.sources <<'EOF'
Types: deb
URIs: https://download.docker.com/linux/ubuntu
Suites: resolute
Components: stable
Architectures: amd64
Signed-By: /etc/apt/keyrings/docker.asc
EOF

# The lock timeout waits for Ubuntu's own apt jobs at first boot instead of failing
export DEBIAN_FRONTEND=noninteractive
apt-get -o DPkg::Lock::Timeout=120 update
apt-get -o DPkg::Lock::Timeout=120 install -y \
  "docker-ce=$DOCKER_VERSION" "docker-ce-cli=$DOCKER_VERSION" "containerd.io=$CONTAINERD_VERSION"
apt-mark hold docker-ce docker-ce-cli containerd.io

TOKEN=$(aws ssm get-parameter --name "$JOIN_TOKEN_PARAMETER" --with-decryption \
  --query Parameter.Value --output text --region "$REGION")
docker swarm join --advertise-addr "$PRIVATE_IP" --token "$TOKEN" "$MANAGER_ADDR:2377"
unset TOKEN
