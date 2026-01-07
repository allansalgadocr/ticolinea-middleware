# RSA Key Pair Configuration Guide

## Overview

This guide explains how to configure RSA key pairs for JWT token signing and verification between the Panel API and Streaming Middleware.

## Key Concepts

### RSA Key Pairs

RSA keys come in **pairs**:
- **Private Key**: Used to **SIGN** tokens (kept secret, only on Panel API)
- **Public Key**: Used to **VERIFY** tokens (can be shared, used by Streaming Middleware)

### Important Rules

1. ✅ You can **always extract** the public key from a private key
2. ❌ You **CANNOT extract** the private key from a public key (this is the security)
3. ✅ The public key is **mathematically derived** from the private key
4. ✅ Both keys must be from the **same key pair** to work together

## Architecture

```
┌─────────────────────┐                    ┌──────────────────────────┐
│   Panel API         │                    │  Streaming Middleware    │
│   (Port 27702)      │                    │  (Port 27701)            │
├─────────────────────┤                    ├──────────────────────────┤
│ Private Key         │   Signs Tokens     │ Public Key               │
│ (JWT:PrivateKey)    │ ──────────────────>│ (JWT:PublicKey)          │
│                     │                    │                          │
│ Generates JWT       │                    │ Verifies JWT             │
│ tokens with         │                    │ tokens and validates     │
│ signature           │                    │ signature                │
└─────────────────────┘                    └──────────────────────────┘
```

## Configuration Requirements

### Panel API Configuration (`ticolinea.panel.API/appsettings.json`)

```json
{
  "Jwt": {
    "Issuer": "ticolinea.panel",
    "Audience": "streaming-node",
    "PrivateKey": "-----BEGIN PRIVATE KEY-----\n...\n-----END PRIVATE KEY-----",
    "AccessTokenExpiryMinutes": 5,
    "RefreshTokenExpiryDays": 364
  }
}
```

**Requirements:**
- Must have `PrivateKey` (not PublicKey)
- Key format: PKCS#8 PEM format
- Used to **sign** JWT tokens

### Streaming Middleware Configuration (`appsettings.json`)

```json
{
  "Jwt": {
    "Issuer": "ticolinea.panel",
    "Audience": "streaming-node",
    "PublicKey": "-----BEGIN PUBLIC KEY-----\n...\n-----END PUBLIC KEY-----",
    "NodeProviderId": "main",
    "PanelApiUrl": "http://tv.play-latino.com:27702/api/v2"
  }
}
```

**Requirements:**
- Must have `PublicKey` (not PrivateKey)
- Key format: PEM format
- Must match the private key from Panel API
- Used to **verify** JWT tokens

## How to Extract Public Key from Private Key

### Method 1: Using OpenSSL Command Line

If you have the private key in a file:

```bash
openssl rsa -in private_key.pem -pubout -out public_key.pem
```

### Method 2: Inline Private Key (from JSON config)

If your private key is stored in JSON with `\n` escape sequences:

```bash
openssl rsa -in <(echo -e "-----BEGIN PRIVATE KEY-----\nMIIEvQIBADANBgkqhkiG9w0BAQEFAASCBKcwggSjAgEAAoIBAQCXOkLcCJuhj7af\n...\n-----END PRIVATE KEY-----") -pubout
```

### Method 3: Process Substitution (Bash)

```bash
# Save private key to variable or file first
PRIVATE_KEY="-----BEGIN PRIVATE KEY-----
MIIEvQIBADANBgkqhkiG9w0BAQEFAASCBKcwggSjAgEAAoIBAQCXOkLcCJuhj7af
...
-----END PRIVATE KEY-----"

# Extract public key
echo "$PRIVATE_KEY" | openssl rsa -in - -pubout
```

### Method 4: From JSON Configuration File

If you need to extract from an existing config file:

```bash
# Extract private key from Panel API config
grep -A 20 '"PrivateKey"' ticolinea.panel/ticolinea.panel.API/appsettings.json | \
  sed 's/.*"PrivateKey": "//' | \
  sed 's/",$//' | \
  sed 's/\\n/\n/g' | \
  openssl rsa -in - -pubout
```

## Step-by-Step Configuration Process

### Step 1: Generate or Obtain Private Key

If you need to generate a new key pair:

```bash
# Generate private key
openssl genpkey -algorithm RSA -out private_key.pem -pkeyopt rsa_keygen_bits:2048

# Extract public key
openssl rsa -in private_key.pem -pubout -out public_key.pem
```

### Step 2: Configure Panel API

1. Copy the **private key** to `ticolinea.panel.API/appsettings.json`
2. Format it as a JSON string with `\n` for newlines:

```json
{
  "Jwt": {
    "PrivateKey": "-----BEGIN PRIVATE KEY-----\nMIIEvQIBAD...\n-----END PRIVATE KEY-----"
  }
}
```

### Step 3: Extract Public Key

Use one of the methods above to extract the public key from the private key.

### Step 4: Configure Streaming Middleware

1. Copy the **public key** to `appsettings.json` (and any provider-specific configs)
2. Format it as a JSON string with `\n` for newlines:

```json
{
  "Jwt": {
    "PublicKey": "-----BEGIN PUBLIC KEY-----\nMIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAlzpC3AiboY+2n9NvaqNG\n...\n-----END PUBLIC KEY-----"
  }
}
```

### Step 5: Verify Configuration

Ensure both configurations have:
- ✅ Matching `Issuer`: `"ticolinea.panel"`
- ✅ Matching `Audience`: `"streaming-node"`
- ✅ Public key matches private key (from same key pair)

## Common Mistakes

### ❌ Wrong: Using Private Key as Public Key

```json
// WRONG - This will fail!
{
  "Jwt": {
    "PublicKey": "-----BEGIN PRIVATE KEY-----\n..."  // ❌ Wrong header!
  }
}
```

**Error you'll see:**
```
[TokenValidation] Failed to load public key: FormatException - The input is not a valid Base-64 string
```

### ❌ Wrong: Mismatched Key Pairs

Using a public key that doesn't match the private key will cause token validation to fail:

```
[TokenValidation] Invalid signature - Key mismatch?
```

### ✅ Correct: Proper Key Configuration

```json
// Panel API
{
  "Jwt": {
    "PrivateKey": "-----BEGIN PRIVATE KEY-----\n..."  // ✅ Correct
  }
}

// Streaming Middleware
{
  "Jwt": {
    "PublicKey": "-----BEGIN PUBLIC KEY-----\n..."  // ✅ Correct
  }
}
```

## Verification

### Verify Keys Match

You can verify that your public and private keys are a matching pair:

```bash
# Create a test file
echo "test message" > test.txt

# Sign with private key
openssl dgst -sha256 -sign private_key.pem -out signature.bin test.txt

# Verify with public key
openssl dgst -sha256 -verify public_key.pem -signature signature.bin test.txt
# Should output: Verified OK
```

### Check Key Format

```bash
# Check private key
openssl rsa -in private_key.pem -text -noout

# Check public key
openssl rsa -pubin -in public_key.pem -text -noout
```

## Troubleshooting

### Issue: "Public key failed to load"

**Symptoms:**
```
[TokenValidation] Failed to load public key: FormatException
[TokenValidation] WARNING: Public key failed to load!
```

**Solutions:**
1. Verify the key has correct headers: `-----BEGIN PUBLIC KEY-----` and `-----END PUBLIC KEY-----`
2. Ensure `\n` escape sequences are properly formatted in JSON
3. Check for extra whitespace or characters
4. Verify the key is actually a public key, not a private key

### Issue: "Invalid signature"

**Symptoms:**
```
[TokenValidation] Invalid signature - Key mismatch?
```

**Solutions:**
1. Verify public key was extracted from the same private key used to sign tokens
2. Check that both keys are from the same key pair
3. Ensure no copy-paste errors in configuration files
4. Regenerate key pair if necessary

### Issue: "Invalid issuer" or "Invalid audience"

**Symptoms:**
```
[TokenValidation] Invalid issuer. Expected: ticolinea.panel, Got: ...
[TokenValidation] Invalid audience. Expected: streaming-node, Got: ...
```

**Solutions:**
1. Ensure `Issuer` matches exactly: `"ticolinea.panel"` (case-sensitive)
2. Ensure `Audience` matches exactly: `"streaming-node"` (case-sensitive)
3. Check for whitespace or typos in configuration

## Security Best Practices

1. **Never commit private keys to version control**
   - Use environment variables or secure key management
   - Add `*key*.pem` to `.gitignore`

2. **Keep private key secure**
   - Only Panel API should have access to private key
   - Use file permissions: `chmod 600 private_key.pem`

3. **Rotate keys periodically**
   - Generate new key pairs on a schedule
   - Update both Panel API and all Streaming Middleware instances

4. **Use strong key sizes**
   - Minimum 2048 bits for RSA keys
   - Consider 4096 bits for higher security

## Quick Reference

| Component | Key Type | Location | Purpose |
|-----------|----------|----------|---------|
| Panel API | Private Key | `appsettings.json` → `Jwt:PrivateKey` | Sign tokens |
| Streaming Middleware | Public Key | `appsettings.json` → `Jwt:PublicKey` | Verify tokens |

## Example: Complete Key Extraction

```bash
# 1. Get private key from Panel API config
PRIVATE_KEY=$(grep -A 1 '"PrivateKey"' ticolinea.panel/ticolinea.panel.API/appsettings.json | \
  tail -1 | sed 's/.*"PrivateKey": "//' | sed 's/",$//')

# 2. Convert \n to actual newlines and extract public key
PUBLIC_KEY=$(echo -e "$PRIVATE_KEY" | openssl rsa -in - -pubout)

# 3. Format for JSON (convert newlines to \n)
PUBLIC_KEY_JSON=$(echo "$PUBLIC_KEY" | sed ':a;N;$!ba;s/\n/\\n/g')

# 4. Output ready for config file
echo "\"PublicKey\": \"$PUBLIC_KEY_JSON\""
```

## Additional Resources

- [OpenSSL RSA Documentation](https://www.openssl.org/docs/man1.1.1/man1/rsa.html)
- [JWT.io - JSON Web Tokens](https://jwt.io/)
- [RSA Cryptography](https://en.wikipedia.org/wiki/RSA_(cryptosystem))

---

**Last Updated:** January 2025  
**Maintained By:** Ticolinea Development Team

