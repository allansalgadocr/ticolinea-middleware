#!/bin/bash

# Script to generate new RSA key pair and update configuration files
# Usage: ./update_keys.sh [--force]

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
MIDDLEWARE_DIR="$PROJECT_ROOT/ticolineapanel/Ticolinea.Streaming.Middleware"
PANEL_API_DIR="$PROJECT_ROOT/ticolinea.panel/ticolinea.panel.API"

TEMP_DIR=$(mktemp -d)
trap "rm -rf $TEMP_DIR" EXIT

PRIVATE_KEY_FILE="$TEMP_DIR/private_key.pem"
PUBLIC_KEY_FILE="$TEMP_DIR/public_key.pem"

echo "=== RSA Key Pair Generator and Configuration Updater ==="
echo ""

# Check if keys already exist
if [ -f "$PRIVATE_KEY_FILE" ] && [ "$1" != "--force" ]; then
    echo "⚠️  Keys already exist. Use --force to regenerate."
    exit 1
fi

# Step 1: Generate new key pair
echo "📝 Step 1: Generating new RSA key pair (2048 bits)..."
openssl genpkey -algorithm RSA -out "$PRIVATE_KEY_FILE" -pkeyopt rsa_keygen_bits:2048

if [ ! -f "$PRIVATE_KEY_FILE" ]; then
    echo "❌ Failed to generate private key"
    exit 1
fi

echo "✅ Private key generated"

# Step 2: Extract public key
echo "📝 Step 2: Extracting public key from private key..."
openssl rsa -in "$PRIVATE_KEY_FILE" -pubout -out "$PUBLIC_KEY_FILE"

if [ ! -f "$PUBLIC_KEY_FILE" ]; then
    echo "❌ Failed to extract public key"
    exit 1
fi

echo "✅ Public key extracted"

# Step 3: Convert keys to JSON format (with \n escape sequences)
echo "📝 Step 3: Converting keys to JSON format..."

PRIVATE_KEY_JSON=$(cat "$PRIVATE_KEY_FILE" | sed ':a;N;$!ba;s/\n/\\n/g')
PUBLIC_KEY_JSON=$(cat "$PUBLIC_KEY_FILE" | sed ':a;N;$!ba;s/\n/\\n/g')

# Step 4: Display keys (first 100 chars for verification)
echo ""
echo "🔑 Generated Keys Preview:"
echo "Private Key (first 100 chars): ${PRIVATE_KEY_JSON:0:100}..."
echo "Public Key (first 100 chars): ${PUBLIC_KEY_JSON:0:100}..."
echo ""

# Step 5: Update Panel API configuration
if [ -f "$PANEL_API_DIR/appsettings.json" ]; then
    echo "📝 Step 4: Updating Panel API configuration..."
    
    # Backup original file
    cp "$PANEL_API_DIR/appsettings.json" "$PANEL_API_DIR/appsettings.json.backup"
    echo "✅ Backup created: appsettings.json.backup"
    
    # Update private key using Python for proper JSON handling
    python3 << EOF
import json
import sys

config_file = "$PANEL_API_DIR/appsettings.json"
private_key = """$PRIVATE_KEY_JSON"""

try:
    with open(config_file, 'r') as f:
        config = json.load(f)
    
    if 'Jwt' not in config:
        config['Jwt'] = {}
    
    config['Jwt']['PrivateKey'] = private_key
    
    with open(config_file, 'w') as f:
        json.dump(config, f, indent=2)
    
    print("✅ Panel API configuration updated")
except Exception as e:
    print(f"❌ Error updating Panel API config: {e}")
    sys.exit(1)
EOF
else
    echo "⚠️  Panel API appsettings.json not found at: $PANEL_API_DIR/appsettings.json"
fi

# Step 6: Update Streaming Middleware configurations
echo "📝 Step 5: Updating Streaming Middleware configurations..."

for config_file in "$MIDDLEWARE_DIR/appsettings.json" "$MIDDLEWARE_DIR/appsettings.main.json" "$MIDDLEWARE_DIR/appsettings.fibraencasa.json"; do
    if [ -f "$config_file" ]; then
        config_name=$(basename "$config_file")
        echo "  Updating $config_name..."
        
        # Backup original file
        cp "$config_file" "${config_file}.backup"
        
        # Update public key using Python
        python3 << EOF
import json
import sys

config_file = "$config_file"
public_key = """$PUBLIC_KEY_JSON"""

try:
    with open(config_file, 'r') as f:
        config = json.load(f)
    
    if 'Jwt' not in config:
        config['Jwt'] = {}
    
    config['Jwt']['PublicKey'] = public_key
    
    with open(config_file, 'w') as f:
        json.dump(config, f, indent=2)
    
    print(f"    ✅ {config_name} updated")
except Exception as e:
    print(f"    ❌ Error updating {config_name}: {e}")
    sys.exit(1)
EOF
    else
        echo "  ⚠️  $config_name not found, skipping..."
    fi
done

# Step 7: Verify keys match
echo ""
echo "📝 Step 6: Verifying key pair..."
echo "test message" > "$TEMP_DIR/test.txt"
openssl dgst -sha256 -sign "$PRIVATE_KEY_FILE" -out "$TEMP_DIR/signature.bin" "$TEMP_DIR/test.txt" 2>/dev/null

if openssl dgst -sha256 -verify "$PUBLIC_KEY_FILE" -signature "$TEMP_DIR/signature.bin" "$TEMP_DIR/test.txt" > /dev/null 2>&1; then
    echo "✅ Key pair verification successful - keys match!"
else
    echo "❌ Key pair verification failed - keys do not match!"
    exit 1
fi

# Step 8: Summary
echo ""
echo "=== Summary ==="
echo "✅ New RSA key pair generated (2048 bits)"
echo "✅ Private key updated in: Panel API appsettings.json"
echo "✅ Public key updated in: Streaming Middleware configs"
echo ""
echo "📋 Updated files:"
echo "  - $PANEL_API_DIR/appsettings.json"
echo "  - $MIDDLEWARE_DIR/appsettings.json"
echo "  - $MIDDLEWARE_DIR/appsettings.main.json"
echo "  - $MIDDLEWARE_DIR/appsettings.fibraencasa.json"
echo ""
echo "💾 Backup files created with .backup extension"
echo ""
echo "⚠️  IMPORTANT: Restart both services after updating keys!"
echo "   - Panel API (port 27702)"
echo "   - Streaming Middleware (port 27701)"
echo ""
echo "🔐 Security Note: Private key is stored in Panel API config."
echo "   Public key is stored in Streaming Middleware configs."
echo "   Both are required for JWT token signing/verification."

