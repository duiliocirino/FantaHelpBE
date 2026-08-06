# Load environment variables from .env (must be in the same directory as this script)
set -a
source "$(dirname "$0")/.env"
set +a

# 1. Create a pod that maps the database port (skip if already exists)
podman pod create --name fantahelp-pod -p 5432:5432 2>/dev/null || true

# 2. Run the Postgres container inside that pod
podman run -d \
  --pod fantahelp-pod \
  --name fantahelp-postgres \
  --restart unless-stopped \
  -e POSTGRES_DB=$POSTGRES_DB \
  -e POSTGRES_USER=$POSTGRES_USER \
  -e POSTGRES_PASSWORD=$POSTGRES_PASSWORD \
  -v fantahelp-postgres-data:/var/lib/postgresql/data:Z \
  docker.io/library/postgres:17-alpine