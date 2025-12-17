# Ticolinea Streaming Middleware - Deployment Guide

## Overview

This streaming middleware serves HLS playlists and manages stream processes. It supports multiple providers (main, subproviders) with provider-specific configurations.

## Architecture

```
┌─────────────────┐     ┌───────────────────┐     ┌─────────────────┐
│  ticolinea.panel│────▶│ Streaming Node    │────▶│  FFmpeg/Streams │
│  (Identity/Auth)│     │ (this service)    │     │                 │
└─────────────────┘     └───────────────────┘     └─────────────────┘
                              │
                              ▼
                        ┌───────────────────┐
                        │   MariaDB/MySQL   │
                        └───────────────────┘
```

## Configuration Files

| File | Purpose |
|------|---------|
| `appsettings.json` | Base configuration (defaults) |
| `appsettings.main.json` | Main provider overrides |
| `appsettings.fibraencasa.json` | Fibra en Casa provider overrides |
| `appsettings.{provider}.json` | Add new providers here |
| `appsettings.{Environment}.json` | Environment-specific overrides (Production, Development, etc.) |

### Configuration Loading Order

The application loads configuration files in the following order (later files override earlier ones):

1. **`appsettings.json`** - Base configuration (always loaded)
2. **`appsettings.{ASPNETCORE_ENVIRONMENT}.json`** - Environment-specific overrides
   - Example: If `ASPNETCORE_ENVIRONMENT=Production`, loads `appsettings.Production.json`
3. **`appsettings.{PROVIDER}.json`** - Provider-specific overrides (if `PROVIDER` is set)
   - Example: If `PROVIDER=main`, loads `appsettings.main.json`
   - Example: If `PROVIDER=fibraencasa`, loads `appsettings.fibraencasa.json`

**Important Notes:**
- `ASPNETCORE_ENVIRONMENT` and `PROVIDER` are **independent** variables
- Setting `ASPNETCORE_ENVIRONMENT=Production` will load `appsettings.Production.json`
- The `PROVIDER` variable controls which provider config file is loaded
- When `PROVIDER=main` (or not set, defaults to "main"), it loads `appsettings.main.json`
- Provider config files override environment config files, so `appsettings.main.json` will override settings from `appsettings.Production.json`

**Example Scenarios:**

| ASPNETCORE_ENVIRONMENT | PROVIDER | Files Loaded |
|------------------------|----------|--------------|
| `Production` | `main` | `appsettings.json` → `appsettings.Production.json` → `appsettings.main.json` |
| `Production` | `fibraencasa` | `appsettings.json` → `appsettings.Production.json` → `appsettings.fibraencasa.json` |
| `Development` | `main` | `appsettings.json` → `appsettings.Development.json` → `appsettings.main.json` |
| `Production` | (not set, defaults to `main`) | `appsettings.json` → `appsettings.Production.json` → `appsettings.main.json` |

## Environment Variables

| Variable | Description | Example |
|----------|-------------|---------|
| `PROVIDER` | Provider ID to load config for | `main`, `fibraencasa` |
| `ASPNETCORE_ENVIRONMENT` | ASP.NET environment | `Production` |
| `ASPNETCORE_URLS` | Listening URLs | `http://0.0.0.0:27701` |

## Ubuntu Deployment

### 1. Prerequisites

```bash
# Install .NET 6 Runtime
wget https://packages.microsoft.com/config/ubuntu/22.04/packages-microsoft-prod.deb
sudo dpkg -i packages-microsoft-prod.deb
sudo apt update
sudo apt install -y dotnet-runtime-6.0 aspnetcore-runtime-6.0

# Install FFmpeg
sudo apt install -y ffmpeg

# Create service user
sudo useradd -r -s /bin/false streamservice
```

### 2. Deploy Application

```bash
# Create directories
sudo mkdir -p /opt/ticolinea/streaming-middleware
sudo mkdir -p /home/ticolineaplay/streams
sudo mkdir -p /var/log/ticolinea

# Copy published files
sudo cp -r ./publish/* /opt/ticolinea/streaming-middleware/

# Set permissions
sudo chown -R streamservice:streamservice /opt/ticolinea/streaming-middleware
sudo chown -R streamservice:streamservice /home/ticolineaplay
sudo chmod -R 755 /opt/ticolinea/streaming-middleware
```

### 3. Configure for Provider

Edit the appropriate appsettings file or create a new one:

```bash
sudo nano /opt/ticolinea/streaming-middleware/appsettings.main.json
```

Key settings to configure:
- `Database.ConnectionString` - Your MariaDB connection string
- `Streaming.StreamsFolder` - Where HLS segments are stored
- `Streaming.SegmentBaseUrl` - Public URL for segment serving
- `Jwt.PublicKey` - RSA public key from ticolinea.panel
- `Jwt.NodeProviderId` - Must match provider ID in panel

### 4. Create Systemd Service

Create `/etc/systemd/system/ticolinea-streaming.service`:

```ini
[Unit]
Description=Ticolinea Streaming Middleware
After=network.target

[Service]
Type=notify
User=streamservice
Group=streamservice
WorkingDirectory=/opt/ticolinea/streaming-middleware
ExecStart=/usr/bin/dotnet /opt/ticolinea/streaming-middleware/ticolinea.stream.service.dll
Restart=always
RestartSec=10
KillSignal=SIGINT
SyslogIdentifier=ticolinea-streaming
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=ASPNETCORE_URLS=http://0.0.0.0:27701
Environment=PROVIDER=main
TimeoutStopSec=30

[Install]
WantedBy=multi-user.target
```

**Note:** With the above configuration:
- `ASPNETCORE_ENVIRONMENT=Production` loads `appsettings.Production.json`
- `PROVIDER=main` loads `appsettings.main.json` (provider configs override environment configs)
- Configuration loading order: `appsettings.json` → `appsettings.Production.json` → `appsettings.main.json`
- Settings in `appsettings.main.json` will override matching settings from `appsettings.Production.json`

### 5. Start Service

```bash
sudo systemctl daemon-reload
sudo systemctl enable ticolinea-streaming
sudo systemctl start ticolinea-streaming

# Check status
sudo systemctl status ticolinea-streaming

# View logs
sudo journalctl -u ticolinea-streaming -f
```

## Adding a New Provider

### 1. Create Provider Config File

```bash
# Copy template
cp appsettings.fibraencasa.json appsettings.newprovider.json

# Edit with provider-specific values
nano appsettings.newprovider.json
```

### 2. Required Configuration

```json
{
  "Database": {
    "ConnectionString": "server=YOUR_DB;..."
  },
  "Streaming": {
    "ProviderId": "newprovider",
    "ProviderName": "New Provider Name",
    "StreamsFolder": "/home/newprovider/streams/",
    "SegmentBaseUrl": "http://newprovider.example.com:27703"
  },
  "Jwt": {
    "NodeProviderId": "newprovider",
    "PanelApiUrl": "https://panel.ticolinea.com/api"
  }
}
```

### 3. Create Service for New Provider

```bash
# Copy service file
sudo cp /etc/systemd/system/ticolinea-streaming.service \
        /etc/systemd/system/ticolinea-streaming-newprovider.service

# Edit to change:
# - Description
# - WorkingDirectory (if different)
# - PROVIDER=newprovider
# - Port (if needed): ASPNETCORE_URLS=http://0.0.0.0:27702

sudo systemctl daemon-reload
sudo systemctl enable ticolinea-streaming-newprovider
sudo systemctl start ticolinea-streaming-newprovider
```

### 4. Register Provider in Panel

Add the provider in ticolinea.panel:
1. Create provider record with `connection_url` pointing to this node
2. Generate JWT signing keys
3. Configure the public key in `appsettings.newprovider.json`

## Nginx Configuration (Optional)

For SSL termination and load balancing:

```nginx
upstream streaming_main {
    server 127.0.0.1:27701;
}

server {
    listen 443 ssl http2;
    server_name stream.ticolinea.com;

    ssl_certificate /etc/ssl/certs/ticolinea.crt;
    ssl_certificate_key /etc/ssl/private/ticolinea.key;

    location / {
        proxy_pass http://streaming_main;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection keep-alive;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_cache_bypass $http_upgrade;
    }
}
```

## Static Segment Server (Port 27703)

The segments are served via a separate static file server. Configure nginx:

```nginx
server {
    listen 27703;
    server_name _;

    location /streams/ {
        alias /home/ticolineaplay/streams/;
        add_header Access-Control-Allow-Origin *;
        add_header Cache-Control "no-cache";
    }
}
```

## Troubleshooting

### Check logs
```bash
sudo journalctl -u ticolinea-streaming -n 100 --no-pager
```

### Verify configuration loaded
Look for startup output:
```
========================================
  STREAMING NODE STARTING
  Provider: main (Ticolinea Main)
========================================
```

### Test endpoints
```bash
# Health check
curl http://localhost:27701/health

# Swagger UI
open http://localhost:27701/swagger
```

### Common issues

1. **JWT validation fails**: Check public key matches panel's private key
2. **Database connection fails**: Verify connection string and network access
3. **Streams not starting**: Check FFmpeg is installed and EnableStreamExecution=true
4. **Segments 404**: Verify SegmentBaseUrl matches nginx config

