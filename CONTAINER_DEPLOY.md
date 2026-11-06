# Run the exported Radarr composite-profile image

These instructions apply to the **destination** machine. It needs Docker (Linux
containers) and Compose, but no source checkout, Node, .NET SDK or build tools.

## First deployment

On the destination Windows machine, double-click **`run-container.bat`** in the exported
folder. It checks the archive checksum, validates Compose, loads the image, then creates
and starts the container using `.env`. It also works from a network share. Docker Desktop
must be running in Linux-container mode. The same launcher recreates the container with
the newly exported image for updates, preserving its config volume.

For manual deployment instead:

Copy the whole exported directory here. In PowerShell, from that directory:

```powershell
# Optional: compare this hash with the accompanying .sha256 file.
Get-FileHash .\radarr-composite-linux-amd64.tar -Algorithm SHA256

docker image load --input .\radarr-composite-linux-amd64.tar
notepad .env
docker compose config
docker compose up -d
docker compose ps
```

The exported `.env` already contains `downloads=C:\Complete` and
`media_root=F:\Media\Movies`. These must be existing folders on the destination machine,
not the build machine. Edit them only if needed. Windows backslashes are supported in
these unquoted values; forward slashes such as `D:/Media/Movies` also work.
On Linux use absolute Linux paths instead. Set `RADARR_PORT` to an available host port.

Open `http://localhost:7878` on this machine (or the configured port), or use this
machine's LAN address from another device. Complete authentication setup and add
`/movies` as the movie root; composite profiles should use `/movies` too.

The deployment Compose file has no build step and never pulls from a registry. If it
reports that the image is missing, load the archive first and check `RADARR_IMAGE` in
`.env` matches the exported image tag. It is an amd64 Linux image.

## Paths, data and connections

- `/config` uses the persistent `radarr-composite_config` Docker volume. This starts
  fresh and does not contain the build machine's dev database or API keys.
- `/movies` maps to `media_root`; `/downloads` maps to `downloads`. Mounting the download
  parent folder makes all client subdirectories beneath it accessible to Radarr.
- The image runs as non-root UID 1654. Docker Desktop must have access to both folders.
  For Linux bind mounts, grant that UID appropriate filesystem permissions.
- `localhost` inside Radarr refers to its own container. For Windows-hosted SABnzbd or
  Prowlarr on the destination Docker Desktop host, use `host.docker.internal`; for
  services on another computer use that computer's address.
- If a downloader reports a Windows path, add a Remote Path Mapping from its reported
  completed-download root to `/downloads/`, using the exact configured client host.
- Composite root settings and movie paths use Linux container paths. Downloader path
  mapping does not convert existing Windows movie paths.
- Do not let another Radarr instance manage the same movie directories simultaneously.

The application updater is disabled. Updates come from newly exported images, preserving
this fork. There is no registry dependency for starting a loaded image.

## Update the running container

Build/export the next version on the build machine and transfer its archive here.
For the same image tag:

```powershell
docker image load --input .\radarr-composite-linux-amd64.tar
docker compose up -d --force-recreate
```

Keep your existing destination `.env` during updates. The exporter preserves an existing
`.env` in its output directory rather than resetting custom values. If the
image tag changes, update `RADARR_IMAGE` in `.env`. The config volume survives recreation.
Back it up before upgrades. `docker compose down` keeps it; `down --volumes` deletes it.

## Smoke test

```powershell
docker compose logs --tail 100 radarr
Invoke-RestMethod http://localhost:7878/ping
docker compose exec radarr /app/bin/ffprobe -version
```

Use your configured host port. Expect `/ping` to return `status: OK` and the container
to become healthy. Verify a small composite movie's imports and restart persistence
before migrating an existing library.

References: [Docker image save](https://docs.docker.com/reference/cli/docker/image/save/),
[Docker image load](https://docs.docker.com/reference/cli/docker/image/load/).
