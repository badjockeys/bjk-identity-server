# pipelines

Scripts used by this repo's CI/CD.

GitHub Actions only runs workflow files from `.github/workflows/`, so the workflow definitions live there and call scripts in this folder.

| Script | Used by | Does |
|---|---|---|
| `scripts/smoke-test.sh` | `.github/workflows/ci.yml` | Runs the identity server against a SQL Server container and tests the User module end to end: register, sign in, update, list, password reset, delete, then restarts to check that startup migrations keep the data. With `APP_IMAGE` set (CI), it runs that container image instead of the build output. |

## What `ci.yml` does

| Job | When | Steps |
|---|---|---|
| `changes` | Every pull request and every push to master | Checks whether anything but documentation changed. When only Markdown files or `docs/` changed, the jobs below are skipped, which still counts as passing |
| `build` | Every pull request and every push to master, unless only documentation changed | Restore, build, unit tests, check migrations, build the container image (x86-64), check it has the liveness model and ffmpeg and doesn't run as root, then the smoke test against that image |
| `publish` | Push to master, after `build` passes | Builds the image again on an x86-64 and an ARM runner (the x86-64 one reuses `build`'s cached layers) and pushes each to GHCR by digest |
| `tag` | After `publish` | Joins the two into one image and tags it `ghcr.io/badjockeys/bjk-identity-server:<full sha>`, `:<first 7 characters>` and `:latest` |

Deployment to Azure (DEV → QA → Staging → Live) will be a separate workflow that takes one of those SHA tags, once the infrastructure (Terraform) is in place.
