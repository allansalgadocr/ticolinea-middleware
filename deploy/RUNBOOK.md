# Ticolinea Provider Node — Runbook

Use this runbook to turn a clean client-owned Ubuntu server into a Ticolinea provider node.
Run every local command from the root of this repository.

For server sizing, required ports, and the WireGuard setup, see
[README.md](README.md). This runbook assumes those requirements have already been agreed with
the client.

## Create a new node

### 1. Collect the required information

Do not begin provisioning until you have all of the following:

- A clean x86-64 server running Ubuntu 22.04 or 24.04.
- A sudo-capable SSH account reachable through WireGuard.
- A public DNS name that resolves to the server.
- Public inbound TCP ports `27701` and `27703`.
- Outbound access to `tv.play-latino.com:27701` and `tv.play-latino.com:27702`.
- Outbound internet access for Ubuntu and Microsoft package downloads during bootstrap.
- The provider's display name and lowercase slug. The slug may contain only `a-z`, `0-9`, and
  hyphens. Use the same slug in the file name, provider config, panel, and every `tico` command.
- The panel JWT public key and shared node API key.
- The package that will be assigned to this provider in the panel.

The examples below use `acme` and version `1.3.2`. Set these shell variables once, replacing the
example values with the real node:

```bash
NODE_SLUG=acme
NODE_VERSION=1.3.2
NODE_SSH_TARGET=ubuntu@10.8.0.4
NODE_PUBLIC_HOST=iptv.acme.cr
```

Use the version number only (`1.3.2`), not the Git tag (`node-v1.3.2`).

### 2. Confirm SSH access

Connect over the client's WireGuard tunnel before using the deployment tool:

```bash
ssh -t "$NODE_SSH_TARGET" 'sudo -v && echo "SSH and sudo OK"'
```

Expect `SSH and sudo OK`. If SSH or sudo fails, fix access before continuing.

The default authentication method is an SSH key. If the client supplied a password instead,
install `sshpass` locally and use one of these modes in the provider config:

- `AUTH_METHOD=password`: reads `deploy/secrets/<slug>.env`, or prompts when it is absent.
- `AUTH_METHOD=ask`: always prompts and never reads or stores a password.

On macOS, install the required password helper with:

```bash
brew install hudochenkov/sshpass/sshpass
```

### 3. Create the provider config

```bash
cp deploy/providers/example.conf "deploy/providers/${NODE_SLUG}.conf"
```

Edit the new file and fill every value:

```ini
SSH_HOST=10.8.0.4
SSH_USER=ubuntu
PROVIDER=acme
PROVIDER_NAME=Acme TV
PUBLIC_HOST=iptv.acme.cr
PANEL_API_URL=http://tv.play-latino.com:27702/api/v2
MAIN_FFMPEG_VERSION=4.4.2
# AUTH_METHOD=key
```

Important details:

- `SSH_HOST` is the WireGuard/tunnel IP, not the public hostname.
- `PROVIDER` must exactly match `NODE_SLUG` and the provider ID created in the panel.
- `PUBLIC_HOST` is a hostname only; do not include `http://`, a path, or a port.
- `PANEL_API_URL` must include `/api/v2`.
- `MAIN_FFMPEG_VERSION` is the production comparison baseline used by `probe`; it does not choose
  which FFmpeg package bootstrap installs.
- Provider configs and secret files are gitignored. Never force-add them to Git.

For `AUTH_METHOD=password`, optionally store the passwords in the gitignored provider secret:

```bash
cp deploy/secrets/provider.env.example "deploy/secrets/${NODE_SLUG}.env"
```

Fill `SSH_PASSWORD` and, if different, `SUDO_PASSWORD`. Skip this file when using key or ask mode.

### 4. Configure the shared panel secrets

This file is shared by all provider nodes. If it already exists and contains the current panel
values, reuse it. Otherwise:

```bash
cp deploy/secrets/shared.env.example deploy/secrets/shared.env
```

Edit `deploy/secrets/shared.env` and set:

```bash
JWT_PUBLIC_KEY='-----BEGIN PUBLIC KEY-----\n...\n-----END PUBLIC KEY-----\n'
PANEL_API_KEY='replace-me'
```

Keep the single quotes and the literal `\n` sequences in the public key. Although the JWT public
key is not private, the panel API key is a secret; never commit or paste this file into logs.

### 5. Probe the server before changing it

```bash
./deploy/tico probe "$NODE_SLUG"
```

`probe` is read-only. Continue only when:

- The OS is Ubuntu 22.04 or 24.04.
- CPU, RAM, and disk meet the agreed sizing.
- Both outbound checks return an HTTP status instead of `UNREACHABLE` or `000`.
- Any FFmpeg version warning has been reviewed. `ffmpeg not installed` is normal on a clean server;
  bootstrap installs it.
- Missing ASP.NET Core 6 is normal on a clean server; bootstrap installs it.

If SSH, sudo, the OS, or outbound connectivity is wrong, stop and fix that first.

### 6. Confirm the firewall rules

Generate the exact request for this node:

```bash
./deploy/tico ports "$NODE_SLUG"
```

Send the output to the client and get confirmation before continuing. MariaDB port `3306` must
never be exposed; it is loopback-only. Also verify that the public DNS name resolves to this server.

### 7. Download and validate a release artifact

Use the ZIP attached to the GitHub Release named `node-v<version>`. Do not use a source-code ZIP or
a local `dotnet publish` directory: the release includes the generated, idempotent `schema.sql`.

With the GitHub CLI authenticated for this private repository:

```bash
NODE_DOWNLOAD_DIR="$(mktemp -d)"
NODE_ARTIFACT_DIR="${NODE_DOWNLOAD_DIR}/release"
mkdir -p "$NODE_ARTIFACT_DIR"

gh release download "node-v${NODE_VERSION}" \
  --repo allansalgadocr/ticolinea-middleware \
  --pattern "node-${NODE_VERSION}.zip" \
  --dir "$NODE_DOWNLOAD_DIR"

unzip -q "${NODE_DOWNLOAD_DIR}/node-${NODE_VERSION}.zip" -d "$NODE_ARTIFACT_DIR"
test -f "${NODE_ARTIFACT_DIR}/schema.sql"
test -f "${NODE_ARTIFACT_DIR}/ticolinea.stream.service.dll"
```

All commands must exit successfully. If downloading through the GitHub web UI instead, unzip the
release asset and use its unpacked directory as `NODE_ARTIFACT_DIR`; perform the same two `test`
checks.

### 8. Bootstrap the server

```bash
./deploy/tico bootstrap "$NODE_SLUG"
```

Bootstrap installs and configures .NET, nginx, MariaDB, FFmpeg, the service account, directories,
the systemd unit, and the nightly 03:00 Costa Rica restart timer. It does not start the streaming
service until a release is deployed. The expected final line is:

```text
Bootstrap complete for acme. Next: tico deploy acme --tag <version>
```

Bootstrap is idempotent, but every rerun rotates the node's database password and rewrites the
stored provider configuration. On a node that already has a release, immediately redeploy after a
bootstrap rerun so the live release receives the new database password.

The optional `--schema` argument is not needed in the normal flow; deploy applies `schema.sql` from
the release artifact.

### 9. Register the provider and package in the panel

Before the first deploy starts the service, create or update the provider in the panel:

- Provider ID/slug: the exact value of `NODE_SLUG` and `PROVIDER`.
- Connection URL: `http://<PUBLIC_HOST>:27701` (for example,
  `http://iptv.acme.cr:27701`).
- Package: assign the provider's intended channel package.

The node requests `/api/v2/providers/<slug>/catalog` on boot. A missing provider, mismatched slug,
or missing package can leave the service healthy but producing zero channels.

### 10. Deploy the first release

Preview the deployment first. A dry run changes nothing:

```bash
./deploy/tico deploy "$NODE_SLUG" \
  --tag "$NODE_VERSION" \
  --artifact "$NODE_ARTIFACT_DIR" \
  --dry-run
```

Then deploy:

```bash
./deploy/tico deploy "$NODE_SLUG" \
  --tag "$NODE_VERSION" \
  --artifact "$NODE_ARTIFACT_DIR"
```

The first deploy applies the database schema, starts the service, checks that `/api/health` returns
HTTP 200, and confirms that the `current` symlink points to the requested version. Because there is
no previously running channel baseline, first-deploy verification is health-only. Complete the
stream checks in the next step; health 200 alone does not prove that the package is populated.

### 11. Verify the node end to end

Run status immediately, then again after the channel startup ramp (normally 2–4 minutes for a large
package):

```bash
./deploy/tico status "$NODE_SLUG"
```

A ready node shows:

- `health: 200`
- `current: <NODE_VERSION>`
- `unit active: active`
- `producing: N channels`, where `N` is greater than zero and is consistent with the assigned
  package

Finally, test the public nginx endpoint from outside the server:

```bash
curl -sS -o /dev/null -w '%{http_code}\n' \
  "http://${NODE_PUBLIC_HOST}:27701/api/health"
```

Expect `200`. If possible, also play one assigned channel from the client network to verify the
full playlist-and-segment path through ports `27701` and `27703`.

Do not declare onboarding complete until the service is active, the release version is correct,
channels are producing, the public health endpoint works, and an actual channel plays.

## First-deploy troubleshooting

### Health is not 200

```bash
./deploy/tico status "$NODE_SLUG"
ssh "$NODE_SSH_TARGET" 'sudo journalctl -u ticolinea-streaming -n 200 --no-pager'
ssh "$NODE_SSH_TARGET" \
  "sudo tail -n 200 /srv/${NODE_SLUG}/logs/TL.\$(date +%Y%m%d).log"
```

Check the first service exception, the MariaDB status, and that the artifact contained both required
files. If the first deploy reports that it was left in place but unverified, there is no older
release to roll back to; fix the cause and rerun the same deploy command.

### Health is 200 but producing is 0

Wait for the initial boot sync and startup ramp, then run status again. If it remains at zero, check:

1. The panel provider ID exactly matches `PROVIDER`.
2. A package with enabled channels is assigned to the provider.
3. `PANEL_API_URL` includes `/api/v2`.
4. The panel API key is current.
5. The node can still reach `tv.play-latino.com:27702` and `:27701`.
6. The application log for `catalog`, `sync`, or authentication errors:

   ```bash
   ssh "$NODE_SSH_TARGET" \
     "sudo grep -Ei 'catalog|sync|auth' /srv/${NODE_SLUG}/logs/TL.\$(date +%Y%m%d).log | tail -n 100"
   ```

After correcting the panel/configuration issue, restart the service to trigger a new boot sync:

```bash
ssh "$NODE_SSH_TARGET" 'sudo systemctl restart ticolinea-streaming'
```

Then allow the channels to ramp and run `./deploy/tico status "$NODE_SLUG"` again.

### The public health check fails but local status is healthy

This isolates the problem to DNS, the client firewall, or nginx. Verify that `PUBLIC_HOST` resolves
to the server, inbound `27701/tcp` is open, and nginx is active. Port `27703/tcp` must also be open
for video segments even though `/api/health` does not exercise it.

## Update a running node

Do not update during prime-time viewing hours. Check the current state first:

```bash
./deploy/tico status "$NODE_SLUG"
```

Download and validate the new release as described above, preview it, and then deploy it:

```bash
./deploy/tico deploy "$NODE_SLUG" --tag "$NODE_VERSION" --artifact "$NODE_ARTIFACT_DIR" --dry-run
./deploy/tico deploy "$NODE_SLUG" --tag "$NODE_VERSION" --artifact "$NODE_ARTIFACT_DIR"
```

The tool stages the release while the current version is serving, swaps the `current` symlink,
restarts the service, and verifies that every channel producing before the swap produces a new
post-restart segment. Recovery is progress-aware because a large node may take 2–4 minutes to ramp.
If verification stalls, the tool automatically rolls back to the previous release.

Verification settings are available for exceptional cases; the defaults are correct for known
nodes:

| Variable | Default | Meaning |
|---|---:|---|
| `TICO_VERIFY_SLEEP` | 5 | Seconds between attempts. |
| `TICO_VERIFY_MIN_TRIES` | 12 | Minimum attempts before a post-progress stall may fail. |
| `TICO_VERIFY_STAGNANT` | 6 | Consecutive attempts with no additional recovered channel before failure. |
| `TICO_VERIFY_ZERO_TRIES` | 24 | Attempts allowed before the first channel recovers. |
| `TICO_VERIFY_TRIES` | 120 | Hard cap (10 minutes with the default sleep). |

## Roll back a bad release

Normal path:

```bash
./deploy/tico rollback "$NODE_SLUG"
./deploy/tico status "$NODE_SLUG"
```

The command repoints `current` to the previous release, restarts the service, and reports health.

Use this manual procedure only when `tico rollback` is unavailable:

```bash
ssh "$NODE_SSH_TARGET"
```

Then run the following commands on the server, replacing the example slug and version:

```bash
NODE_SLUG=acme
PREVIOUS_VERSION=1.3.1
ls -1dt "/opt/${NODE_SLUG}/releases/"*/
sudo ln -sfn "/opt/${NODE_SLUG}/releases/${PREVIOUS_VERSION}" "/opt/${NODE_SLUG}/current.tmp"
sudo mv -T "/opt/${NODE_SLUG}/current.tmp" "/opt/${NODE_SLUG}/current"
sudo systemctl restart ticolinea-streaming
curl -s -o /dev/null -w '%{http_code}\n' http://127.0.0.1:1234/api/health
```

Expect HTTP `200`, exit the SSH session, then confirm that channels recover with
`./deploy/tico status "$NODE_SLUG"`.

## Runtime flags

- `Streaming:FfmpegManagedDiscontinuities` is `true` by default. If devices on one node freeze
  after channel restarts, set it to `false` in
  `/opt/<slug>/config/appsettings.<slug>.json`, then redeploy so the release copy receives the
  change.
- `Watchdog:Enabled` is `true` by default. It kills a wedged FFmpeg process and relaunches it within
  a restart budget. Use the same edit-and-redeploy procedure for a per-node override.

## If an update interrupts a client's channels

1. Roll back immediately.
2. Confirm `status` shows health 200, the expected previous version, and producing channels.
3. Confirm a real channel plays.
4. Tell the client there was a brief interruption during a software update, service is restored,
   and the cause is under review.

## Known deferred items

- The Hangfire dashboard is reachable through nginx and is unauthenticated, matching the main node.
- .NET 6 is end-of-life; provider nodes currently run it to match production.
- Repository-managed historical configuration still contains secrets; provider deploys strip
  non-provider appsettings from the release before it reaches a client node.
