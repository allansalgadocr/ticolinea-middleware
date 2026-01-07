# Key Management Scripts

This directory contains scripts to help manage RSA key pairs for JWT token signing and verification.

## Available Scripts

### 1. `update_keys.sh` - Generate New Key Pair

Generates a completely new RSA key pair and updates all configuration files.

**Usage:**
```bash
cd ticolineapanel/Ticolinea.Streaming.Middleware/scripts
./update_keys.sh
```

**What it does:**
1. Generates a new 2048-bit RSA private key
2. Extracts the matching public key
3. Updates Panel API `appsettings.json` with the private key
4. Updates all Streaming Middleware configs with the public key
5. Creates backup files (`.backup` extension)
6. Verifies the key pair matches

**Options:**
- `--force`: Regenerate keys even if they already exist

**Example:**
```bash
./update_keys.sh --force
```

### 2. `extract_public_key.sh` - Extract Public Key from Existing Private Key

Extracts the public key from the private key in Panel API config and updates middleware configs.

**Usage:**
```bash
cd ticolineapanel/Ticolinea.Streaming.Middleware/scripts
./extract_public_key.sh
```

**What it does:**
1. Reads private key from Panel API `appsettings.json`
2. Extracts the matching public key using OpenSSL
3. Updates all Streaming Middleware configs with the public key
4. Creates backup files

**Use this when:**
- You have an existing private key in Panel API
- You need to update middleware configs with the matching public key
- You don't want to generate new keys (keeps existing tokens valid)

## Prerequisites

- OpenSSL installed (`openssl` command)
- Python 3 installed (`python3` command)
- Bash shell
- Access to both Panel API and Streaming Middleware configuration directories

## Safety Features

- ✅ Automatic backup creation (`.backup` files)
- ✅ Key pair verification before completion
- ✅ Error handling and rollback information
- ✅ Preview of keys before updating

## After Running Scripts

**IMPORTANT:** Restart both services after updating keys:

```bash
# Restart Panel API
sudo systemctl restart ticolinea-panel-api.service  # or your service name

# Restart Streaming Middleware
sudo systemctl restart ticolinea.service  # or your service name
```

## Troubleshooting

### Script fails with "openssl: command not found"
Install OpenSSL:
```bash
# Ubuntu/Debian
sudo apt-get install openssl

# macOS
brew install openssl
```

### Script fails with "python3: command not found"
Install Python 3:
```bash
# Ubuntu/Debian
sudo apt-get install python3

# macOS (usually pre-installed)
# If not, install via Homebrew
brew install python3
```

### Permission denied
Make scripts executable:
```bash
chmod +x update_keys.sh
chmod +x extract_public_key.sh
```

### Configuration files not found
Ensure you're running the script from the correct directory and that the project structure matches:
```
ticolinea/
├── ticolinea.panel/
│   └── ticolinea.panel.API/
│       └── appsettings.json
└── ticolineapanel/
    └── Ticolinea.Streaming.Middleware/
        ├── appsettings.json
        ├── appsettings.main.json
        └── appsettings.fibraencasa.json
```

## Manual Key Management

If you prefer to manage keys manually, see the main documentation:
- [KEY_PAIR_CONFIGURATION.md](../KEY_PAIR_CONFIGURATION.md)

## Security Notes

- ⚠️  Private keys are sensitive - never commit them to version control
- ⚠️  Backup files contain keys - secure them appropriately
- ⚠️  Rotate keys periodically for security
- ⚠️  Use strong key sizes (2048+ bits)

