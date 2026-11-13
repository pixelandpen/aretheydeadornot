# Are They Dead Or Not?

A production-style proof-of-concept answering *"Is this person dead or not?"* in sub-100ms, built with:
- **Backend:** C# / .NET 9 Minimal APIs + Dapper + `HybridCache` (L1 In-Memory + L2 Redis)
- **Database:** PostgreSQL 17 Primary + Streaming Read Replica (bulk-migrated from your 2.29M-row Wikidata SQLite database)
- **Frontend:** React + TypeScript + Vite
- **Load Balancing & Orchestration:** NGINX Load Balancer (`docker-compose`), Kubernetes (`k8s/`), and Terraform for Azure AKS (`infra/terraform/`)
- **Testing & CI/CD:** xUnit unit tests + GitHub Actions PR gate

---

## 1. Quickstart (Local Docker Compose)

1. **Start the infrastructure, load balancer, API replicas, and React UI:**
   ```bash
   docker compose up -d --build
   ```

2. **Migrate your Wikidata SQLite database into PostgreSQL Primary** (streams ~2.29M people + ~2.58M occupations via `COPY` and builds indexes; takes ~1–2 minutes):
   ```bash
   docker compose --profile etl run --rm etl
   ```

3. **Open the app:**
   - Web UI: http://localhost:8080
   - API Search: `http://localhost:8080/api/v1/search?name=Michael%20Jackson`
   - Recent Deaths: `http://localhost:8080/api/v1/recent-deaths`
   - In Memory (±3 days): `http://localhost:8080/api/v1/in-memory`
   - Health Readiness: `http://localhost:8080/health/ready`

---

## 2. Running Unit Tests (xUnit)

With Docker (no local .NET SDK required):
```bash
docker run --rm -v "$(pwd):/src" -w /src mcr.microsoft.com/dotnet/sdk:9.0 dotnet test
```

---

## 3. Local Kubernetes Testing

If you have Docker Desktop Kubernetes, `kind`, or `k3d` running with `ingress-nginx`:
```bash
docker build -f src/Api/Dockerfile -t aretheydeadornot-api:latest .
docker build -f src/Web/Dockerfile -t aretheydeadornot-web:latest src/Web
kubectl apply -k k8s/overlays/local
```

---

## 4. Deploying to Azure (AKS + Managed PostgreSQL Read Replica + Redis)

1. **Authenticate with Azure CLI:**
   ```bash
   az login
   ```
2. **Configure Terraform variables:**
   ```bash
   cd infra/terraform
   cp terraform.tfvars.example terraform.tfvars
   # Edit terraform.tfvars with a strong postgres_admin_password
   ```
3. **Provision infrastructure:**
   ```bash
   terraform init
   terraform plan
   terraform apply
   ```
4. **Create Kubernetes secrets for Azure PostgreSQL Read Replica & Redis** (using values from `terraform output`):
   ```bash
   az aks get-credentials --resource-group rg-aretheydeadornot --name aks-aretheydeadornot
   kubectl create namespace aretheydeadornot
   kubectl -n aretheydeadornot create secret generic app-secrets \
     --from-literal=postgres-read-replica="Host=<REPLICA_FQDN>;Port=5432;Database=celebrities;Username=pgadmin;Password=<PASSWORD>;Ssl Mode=Require" \
     --from-literal=redis="<REDIS_HOST>:6380,password=<REDIS_KEY>,ssl=True,abortConnect=False"
   ```
5. **Tear down when finished demoing** (avoids ongoing Azure charges):
   ```bash
   terraform destroy
   ```

---

## 5. GitHub CI/CD & Branch Protection

1. Create a private GitHub repository and push this code.
2. In GitHub **Settings → Branches → Add branch protection rule** for `main`:
   - Enable **Require a pull request before merging**.
   - Enable **Require status checks to pass before merging** and select:
     - `.NET Build & xUnit Tests`
     - `React Typecheck & Build`
     - `Terraform Fmt & Validate`
