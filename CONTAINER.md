# Build and export the Docker image

This checkout is the build workspace. The resulting image is transferred to a separate
amd64 Docker host. The build script does not start Radarr here.

## Build on this machine

Docker CLI must have access to a running Linux Docker engine, such as Docker Desktop
in Linux-container mode. From this checkout:

```powershell
.\scripts\build-container.bat
```

To build and then move the verified deployment files directly to
`\\server\Server\docker\radarr49`, use:

```powershell
.\scripts\build-container-to-server.bat
```

This checks share access first, stages and verifies every transferred file, then removes
the local copies after publishing them successfully. An existing server `.env` is kept.
A failed build or transfer keeps the local build outputs. This does not start or restart
the server's container.

For an already-configured remote Docker context instead:

```powershell
.\build-container.ps1 -DockerContext my-builder
```

The build uses the pinned .NET SDK, Node and the repository lockfile to compile both
the backend and production frontend, including the modified header logo. The image
contains the .NET runtime, POSIX assembly, ffprobe and SQLite dependencies. In-app
updates are disabled so upstream updates cannot replace the fork.

## Transfer the output

Copy the whole `_artifacts/container/` directory to the destination machine:

| File | Purpose |
| --- | --- |
| `radarr-composite-linux-amd64.tar` | Docker image archive |
| `radarr-composite-linux-amd64.tar.sha256` | Archive checksum |
| `compose.yaml` | Destination-only Compose configuration; no build step |
| `.env` | Destination folders, port and image tag; created with defaults |
| `README.md` | Destination instructions |
| `run-container.bat` | Verify/load the image and create/start the destination container |

Follow the exported README or [CONTAINER_DEPLOY.md](CONTAINER_DEPLOY.md) on the
destination host. That machine needs Docker and Compose, but no source checkout,
Node or .NET SDK. Its folders and connections are configured there, not here.

`compose.deploy.yaml`, `container.defaults.env` and `CONTAINER_DEPLOY.md` are the source
templates for those deployment files. The exporter keeps an existing destination
`.env` untouched when refreshing an output directory.

The default paths are `downloads=C:\Complete` and `media_root=F:\Media\Movies`.

Re-run the build/export script for updates, transfer the new archive, load it on the
destination and recreate its container. The destination config volume persists.

The Docker context allowlist excludes dev configuration, databases, API-key files,
local build outputs, dependencies and repository metadata. No dev config or library
files are included in the transfer package.

## Validation

On November 6, 2026 the Linux amd64 image passed a real Docker build, export and
archive-load check. Its checksum, architecture, non-root user, entrypoint, updater
settings and destination Compose were verified. Radarr was not started here;
application startup/import checks belong on the destination host.

Evidence: `_temp/container-image-build.log` and
`_temp/container-image-verification.json`. Export orchestration was also checked
under PowerShell 7 and Windows PowerShell 5.1 with a Docker test double, including
remote contexts and stopping after engine/build failures.

References: [Docker image save](https://docs.docker.com/reference/cli/docker/image/save/),
[Docker image load](https://docs.docker.com/reference/cli/docker/image/load/).
