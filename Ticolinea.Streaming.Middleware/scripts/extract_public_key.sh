#!/bin/bash

# Script to extract public key from existing private key in Panel API config
# Usage: ./extract_public_key.sh

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"
PANEL_API_DIR="$PROJECT_ROOT/ticolinea.panel/ticolinea.panel.API"
MIDDLEWARE_DIR="$PROJECT_ROOT/ticolineapanel/Ticolinea.Streaming.Middleware"

TEMP_DIR=$(mktemp -d)
trap "rm -rf $TEMP_DIR" EXIT

echo "=== Extract Public Key from Panel API Private Key ==="
echo ""

# Check if Panel API config exists
if [ ! -f "$PANEL_API_DIR/appsettings.json" ]; then
    echo "❌ Panel API appsettings.json not found at: $PANEL_API_DIR/appsettings.json"
    exit 1
fi

# Step 1: Extract private key from config
echo "📝 Step 1: Extracting private key from Panel API configuration..."

python3 << EOF
import json
import sys
import subprocess
import tempfile
import os

config_file = "$PANEL_API_DIR/appsettings.json"
temp_private = "$TEMP_DIR/private_key.pem"
temp_public = "$TEMP_DIR/public_key.pem"

try:
    # Read config
    with open(config_file, 'r') as f:
        config = json.load(f)
    
    if 'Jwt' not in config or 'PrivateKey' not in config['Jwt']:
        print("❌ PrivateKey not found in Panel API configuration")
        sys.exit(1)
    
    private_key_raw = config['Jwt']['PrivateKey']
    
    # Convert \n escape sequences to actual newlines
    private_key = private_key_raw.replace('\\n', '\n')
    
    # Write to temp file
    with open(temp_private, 'w') as f:
        f.write(private_key)
    
    print("✅ Private key extracted")
    
    # Extract public key using openssl
    result = subprocess.run(
        ['openssl', 'rsa', '-in', temp_private, '-pubout', '-out', temp_public],
        capture_output=True,
        text=True
    )
    
    if result.returncode != 0:
        print(f"❌ Failed to extract public key: {result.stderr}")
        sys.exit(1)
    
    # Read public key
    with open(temp_public, 'r') as f:
        public_key = f.read()
    
    # Convert to JSON format
    public_key_json = public_key.replace('\n', '\\n')
    
    print("✅ Public key extracted")
    print("")
    print("🔑 Public Key (first 100 chars):")
    print(f"   {public_key_json[:100]}...")
    print("")
    
    # Update middleware configs
    config_files = [
        "$MIDDLEWARE_DIR/appsettings.json",
        "$MIDDLEWARE_DIR/appsettings.main.json",
        "$MIDDLEWARE_DIR/appsettings.fibraencasa.json"
    ]
    
    updated_count = 0
    for config_file_path in config_files:
        if os.path.exists(config_file_path):
            config_name = os.path.basename(config_file_path)
            print(f"📝 Updating {config_name}...")
            
            # Backup
            backup_file = f"{config_file_path}.backup"
            with open(config_file_path, 'r') as f:
                backup_content = f.read()
            with open(backup_file, 'w') as f:
                f.write(backup_content)
            
            # Update config
            with open(config_file_path, 'r') as f:
                middleware_config = json.load(f)
            
            if 'Jwt' not in middleware_config:
                middleware_config['Jwt'] = {}
            
            middleware_config['Jwt']['PublicKey'] = public_key_json
            
            with open(config_file_path, 'w') as f:
                json.dump(middleware_config, f, indent=2)
            
            print(f"   ✅ {config_name} updated")
            updated_count += 1
        else:
            print(f"   ⚠️  {os.path.basename(config_file_path)} not found, skipping...")
    
    print("")
    print("=== Summary ===")
    print(f"✅ Public key extracted from Panel API private key")
    print(f"✅ Updated {updated_count} middleware configuration file(s)")
    print("")
    print("💾 Backup files created with .backup extension")
    print("")
    print("⚠️  IMPORTANT: Restart Streaming Middleware after updating keys!")
    
except Exception as e:
    print(f"❌ Error: {e}")
    import traceback
    traceback.print_exc()
    sys.exit(1)
EOF

