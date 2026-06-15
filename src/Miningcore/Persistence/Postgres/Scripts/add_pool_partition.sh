#!/bin/bash
# Usage: bash add_pool_partition.sh <pool_id> [host] [user]
# Example: bash add_pool_partition.sh bte1
# Example: bash add_pool_partition.sh bte1 127.0.0.1 miningcore

POOL_ID="${1}"
DB_HOST="${2:-127.0.0.1}"
DB_USER="${3:-miningcore}"
DB_NAME="miningcore"

if [ -z "$POOL_ID" ]; then
    echo "Error: pool_id is required"
    echo "Usage: bash add_pool_partition.sh <pool_id> [host] [user]"
    echo "Example: bash add_pool_partition.sh bte1"
    exit 1
fi

echo "Creating shares partition for pool: $POOL_ID ..."

psql -h "$DB_HOST" -U "$DB_USER" -d "$DB_NAME" -c \
    "CREATE TABLE IF NOT EXISTS shares_${POOL_ID} PARTITION OF shares FOR VALUES IN ('${POOL_ID}');"

if [ $? -eq 0 ]; then
    echo "Done. Partition shares_${POOL_ID} created."
else
    echo "Error creating partition."
    exit 1
fi
