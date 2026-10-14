# pipelines

Scripts used by this repo's CI/CD.

GitHub Actions only runs workflow files from `.github/workflows/`, so the workflow definitions live there and call scripts in this folder.

| Script | Used by | Does |
|---|---|---|
| `scripts/smoke-test.sh` | `.github/workflows/ci.yml` | Runs the identity server against a SQL Server container and tests the User module end to end: register, sign in, update, list, password reset, delete, then restarts to check that startup migrations keep the data. |

Deployment to Azure (DEV → QA → Staging → Live) will be added here once the infrastructure is in place.
