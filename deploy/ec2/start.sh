#!/usr/bin/env bash
# Build the image, apply migrations, start the API, then wait for /health.
# Usage (on the server): sudo bash ~/fleet/deploy/ec2/start.sh
set -euo pipefail

cd "$(dirname "$0")"
REGION=ap-south-1
SHARED_NETWORK=ec2_default
DB_SECRET=database/postgresql/routego-fleet-api

if ! docker network inspect "$SHARED_NETWORK" >/dev/null 2>&1; then
  echo "ERROR: Docker network $SHARED_NETWORK not found. Is the Postgres container (ec2-postgres-1) running?" >&2
  exit 1
fi

# The migrate container needs the database password; the instance role reads it from the same
# secret the API uses, so it is never written to a file on this server.
echo ">> Reading the database password from Secrets Manager"
FLEET_DB_PASSWORD="$(aws secretsmanager get-secret-value --secret-id "$DB_SECRET" --region "$REGION" \
  --query SecretString --output text | python3 -c 'import json, sys; print(json.load(sys.stdin)["password"])')"
export FLEET_DB_PASSWORD

echo ">> Building image (first time takes 5-10 minutes)"
docker build -t routego/fleet:latest ../..

echo ">> Applying migrations and starting the API"
if ! docker compose up -d; then
  echo "ERROR: migrations or API start failed. Last logs:" >&2
  docker compose ps -a
  docker compose logs --tail 50 migrate fleet
  exit 1
fi

echo ">> Waiting for the API to be healthy"
for _ in $(seq 1 30); do
  if curl -fs http://localhost:5002/health >/dev/null 2>&1; then
    docker compose ps -a
    echo ">> DEPLOY OK — Fleet API is healthy on port 5002"
    exit 0
  fi
  sleep 2
done

echo "ERROR: Fleet API did not become healthy. Last logs:" >&2
docker compose ps -a
docker compose logs --tail 50 migrate fleet
exit 1
