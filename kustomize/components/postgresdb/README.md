# Integrate Online Boutique with PostgreSQL

By default the `cartservice` stores its data in an in-cluster Redis database. This component replaces Redis with an in-cluster **PostgreSQL** database for the cart, giving you a relational store with ACID transactions.

The `cartservice` automatically creates the required `cart_items` table on startup.

## Deploy Online Boutique with PostgreSQL

From the `kustomize/` folder at the root level of this repository, add the component:

```bash
kustomize edit add component components/postgresdb
```

This will update `kustomize/kustomization.yaml` to look like:

```yaml
apiVersion: kustomize.config.k8s.io/v1beta1
kind: Kustomization
resources:
- base
components:
- components/postgresdb
```

Then deploy:

```bash
kubectl apply -k .
```

This component will:
- Deploy a PostgreSQL 16 StatefulSet in the cluster (`postgres-cart`) with a 1Gi PersistentVolumeClaim
- Patch `cartservice` to use `POSTGRES_CONN_STRING` instead of `REDIS_ADDR`
- Remove the `redis-cart` Deployment and Service

## Customizing credentials

Edit the connection string in `components/postgresdb/kustomization.yaml` and the matching environment variables in `components/postgresdb/postgres-cart.yaml`. Keep them in sync:

| Parameter | kustomization.yaml (connection string) | postgres-cart.yaml (env var) |
|---|---|---|
| Database name | `Database=cart` | `POSTGRES_DB=cart` |
| Username | `Username=cartuser` | `POSTGRES_USER=cartuser` |
| Password | `Password=cartpass` | `POSTGRES_PASSWORD=cartpass` |

## Using an external PostgreSQL instance

To point at an external PostgreSQL (e.g. Cloud SQL, RDS, or a self-managed instance):

1. Remove the `postgres-cart.yaml` from the `resources` list in `kustomization.yaml`
2. Update the `POSTGRES_CONN_STRING` value in the cartservice patch to your external connection string:
   ```
   Host=<your-host>;Port=5432;Database=<db>;Username=<user>;Password=<pass>
   ```

## Storage

PostgreSQL runs as a StatefulSet with a `volumeClaimTemplate` that provisions a 1Gi PersistentVolumeClaim. Data survives pod restarts. To change the size, edit the `storage` field in the `volumeClaimTemplates` section of `postgres-cart.yaml`.
