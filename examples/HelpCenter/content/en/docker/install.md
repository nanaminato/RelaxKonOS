# Install Docker

This guide installs Docker on a Windows or Linux host running RelaxKonOS RelaxKonOS.Server. Prefer Docker's official installer or package repository, and follow your organization's image-registry and proxy policies.

## Before you begin

- Confirm that you have administrator access and the host meets Docker's system requirements.
- Check that the host can reach Docker's package repository, or that an approved mirror is configured.
- Account for older Docker packages or Docker Desktop installations before installing a newer version.
- For production, reserve adequate disk space for images, container logs, and volumes.

## Windows

1. Install the Docker Desktop or Docker Engine edition appropriate for Windows 10/11 or Windows RelaxKonOS.Server.
2. Docker Desktop normally requires WSL 2 or Hyper-V; follow the installer prompts to enable them and restart if requested.
3. Start Docker Desktop (or the Docker service) and verify that it reports as running.

If RelaxKonOS Server runs inside WSL, a virtual machine, or a container, make sure that environment can reach the Docker daemon or socket.

## Linux

1. Follow Docker's official instructions for your distribution to add its repository and install Docker Engine, the CLI, and the Compose plugin.
2. Start Docker and enable it at boot. For a systemd host:

```bash
sudo systemctl enable --now docker
```

3. To allow a non-root user to run Docker, add that user to the `docker` group and sign in again. This grants privileges close to root on the host.

## Verify the installation

Run these commands on either Windows or Linux:

```bash
docker --version
docker info
```

They should display both client and server information. If the server cannot be reached, first check that Docker Desktop or the Docker service is running.

## Verify in RelaxKonOS

Open Docker Manager and wait for the status card to show that the engine is available. If it remains unavailable, inspect the Docker service logs and confirm that the account running RelaxKonOS Server can access the Docker socket (Linux) or Docker Desktop/Engine (Windows).
