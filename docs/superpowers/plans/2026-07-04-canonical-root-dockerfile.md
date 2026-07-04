# Canonical Root Dockerfile Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the root Dockerfile the sole API image definition and consistently target `services/SonicRelay.Api/SonicRelay.Api.csproj` in Docker, Compose, CI/CD, and documentation.

**Architecture:** The repository root remains the Docker build context so all referenced projects are available. Compose and GitHub Actions share the root Dockerfile and exact API project path; production deployment continues consuming the published image.

**Tech Stack:** Docker multi-stage builds, Docker Compose, .NET 10, GitHub Actions, Markdown

---

### Task 1: Canonicalize Docker and Compose

**Files:**
- Modify: `Dockerfile`
- Delete: `services/SonicRelay.Api/Dockerfile`
- Modify: `infra/compose.yml`

- [ ] **Step 1: Confirm the current broken path**

Run: `Select-String -Path Dockerfile -Pattern 'src/SonicRelay.Api/SonicRelay.Api.csproj'`
Expected: one match proving the root Dockerfile has the stale path.

- [ ] **Step 2: Change the default project and Compose Dockerfile**

Set `ARG APP_PROJECT=services/SonicRelay.Api/SonicRelay.Api.csproj` in `Dockerfile`, set `dockerfile: Dockerfile` in `infra/compose.yml`, and delete `services/SonicRelay.Api/Dockerfile`.

- [ ] **Step 3: Verify static references**

Run: `git grep -n 'src/SonicRelay.Api/SonicRelay.Api.csproj\|services/SonicRelay.Api/Dockerfile' -- Dockerfile infra services ':!.git'`
Expected: no matches.

### Task 2: Make CI Use the Exact Project

**Files:**
- Modify: `.github/workflows/vps-ci-cd.yml`

- [ ] **Step 1: Set the canonical project path**

Change `APP_PROJECT` to `services/SonicRelay.Api/SonicRelay.Api.csproj`. Replace fallback discovery with a check that emits an Actions error and exits when the file is absent, then output the configured path for downstream jobs.

- [ ] **Step 2: Preserve job separation and image inputs**

Keep the `build`, `test`, `publish_image`, and `deploy` jobs. Keep the root `Dockerfile` and pass `APP_PROJECT` to the image build.

- [ ] **Step 3: Check workflow references**

Run: `Select-String -Path .github/workflows/vps-ci-cd.yml -Pattern 'services/SonicRelay.Api/SonicRelay.Api.csproj|file: ./Dockerfile|^  (build|test|publish_image|deploy):'`
Expected: canonical path, root Dockerfile, and all four jobs appear.

### Task 3: Align Documentation

**Files:**
- Modify: `README.md`
- Modify: `docs/deployment-vps-ssh.md`

- [ ] **Step 1: Document the canonical build**

Add a concise README statement that `docker build .` uses the root Dockerfile and exact API project. Update the deployment workflow description to say build uses the configured project directly and image publication uses the root Dockerfile.

- [ ] **Step 2: Remove obsolete fallback wording**

Run: `git grep -n 'legacy path\|discovers the first non-test' -- README.md docs/deployment-vps-ssh.md`
Expected: no matches.

### Task 4: Focused Validation

**Files:**
- Verify only; no planned modifications

- [ ] **Step 1: Validate .NET restore/build/test**

Run the exact project-scoped restore, Release build, and integration-test commands from the design spec. Expected: exit code 0.

- [ ] **Step 2: Validate Docker image**

Run: `docker build .`
Expected: image builds successfully with `SonicRelay.Api.dll` and a non-root runtime.

- [ ] **Step 3: Validate Compose models**

Run the dev, full production, and API-only production Compose config commands from the design spec. Expected: exit code 0 for each.

- [ ] **Step 4: Review scoped diff**

Run: `git diff --stat; git diff --check`
Expected: only planned files changed, deleted, or added; no whitespace errors.
