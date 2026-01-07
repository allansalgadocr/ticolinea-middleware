# Authentication System - Streaming Middleware

## Overview

The Streaming Middleware validates JWT tokens issued by the Panel API (`ticolinea.panel`) to authorize stream access. It acts as a **token validator** and **proxy** for token refresh operations.

## Architecture

```
┌─────────────────┐                    ┌──────────────────┐
│  StreamTV App   │                    │  Streaming       │
│  (Client)       │                    │  Middleware      │
└────────┬────────┘                    └────────┬─────────┘
         │                                       │
         │ 1. Request streams with token         │
         │    GET /Streams/PlaylistByToken/PlaylistByToken?token=... │
         ├───────────────────────────────────────▶
         │                                       │
         │                                       │ 2. Validate JWT token
         │                                       │    - Check signature
         │                                       │    - Check expiration
         │                                       │    - Extract claims
         │                                       │
         │ 3. Return playlist                    │
         │◀───────────────────────────────────────
         │                                       │
         │ 4. Token expired? Request refresh    │
         │    POST /Auth/Refresh                 │
         ├───────────────────────────────────────▶
         │                                       │
         │                                       │ 5. Validate refresh token
         │                                       │    locally first
         │                                       │
         │                                       │ 6. Call Panel API
         │                                       │    POST {PanelApiUrl}/auth/refresh
         │                                       ├──────────────┐
         │                                       │              │
         │                                       │              ▼
         │                                       │    ┌──────────────────┐
         │                                       │    │  Panel API       │
         │                                       │    │  (ticolinea.panel)│
         │                                       │    │  - Validates token│
         │                                       │    │  - Checks user    │
         │                                       │    │    is still active│
         │                                       │    │  - Returns new    │
         │                                       │    │    tokens         │
         │                                       │    └────────┬─────────┘
         │                                       │             │
         │                                       │◀─────────────┘
         │                                       │ 7. New tokens
         │                                       │
         │ 8. Return new tokens to client        │
         │◀───────────────────────────────────────
         │                                       │
```

## Token Validation

### Access Token Validation

When a request comes in with a JWT token, the middleware:

1. **Extracts token** from query parameter or Authorization header
2. **Validates signature** using RSA public key from Panel API
3. **Checks expiration** (access tokens expire in 5 minutes)
4. **Validates issuer** (`ticolinea.panel`)
5. **Validates audience** (`streaming-node`)
6. **Checks token type** (must be "access", not "refresh")
7. **Validates provider ID** matches this node's `NodeProviderId`
8. **Extracts claims** (user ID, packages, provider URL, etc.)

### Token Claims Used

- `sub`: User identifier (Client ID)
- `providerId`: Must match `NodeProviderId` in config
- `providerUrl`: Base URL for this provider's streaming node
- `packageIds`: Packages user has access to
- `moviesAllowed`: Whether user can access VOD content
- `mac`: MAC address binding (if any)

## Authentication Endpoints

### 1. Refresh Token

**Endpoint:** `POST /Auth/Refresh`

**Request:**
```json
{
  "refreshToken": "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9..."
}
```

**Response (Success):**
```json
{
  "access_token": "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9...",
  "refresh_token": "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9...",
  "expires_in": 300,
  "token_type": "Bearer"
}
```

**Response (Error):**
```json
{
  "error": "refresh_failed",
  "message": "Unable to refresh token. User may be inactive or deleted."
}
```

**Process:**
1. Validates refresh token signature locally (fast check)
2. Calls Panel API: `POST {PanelApiUrl}/auth/refresh`
3. Panel API validates token and checks if user is still active
4. Returns new tokens from Panel API to client

### 2. Validate Token

**Endpoint:** `GET /Auth/Validate?token=...`

**Response:**
```json
{
  "valid": true,
  "sub": "123",
  "providerId": "main",
  "packageIds": ["package1", "package2"],
  "moviesAllowed": true,
  "mac": "00:11:22:33:44:55"
}
```

### 3. Token Status

**Endpoint:** `GET /Auth/Status?token=...`

**Response:**
```json
{
  "valid": true,
  "needsRefresh": false
}
```

## Communication with Panel API

### Token Refresh Flow

The middleware **proxies** refresh requests to the Panel API:

1. **Client** sends refresh request to Streaming Middleware
2. **Streaming Middleware** validates refresh token signature locally
3. **Streaming Middleware** calls Panel API:
   ```
   POST {PanelApiUrl}/auth/refresh
   Body: { "refreshToken": "..." }
   ```
4. **Panel API** validates token and checks user status
5. **Panel API** returns new tokens if user is active
6. **Streaming Middleware** returns tokens to client

**Why this design?**
- Panel API is the source of truth for user status
- Users can be deactivated/deleted in Panel
- Refresh must verify user is still active
- Streaming nodes don't need direct database access

### Configuration

**Panel API URL** is configured in `appsettings.main.json`:

```json
{
  "Jwt": {
    "PanelApiUrl": "http://tv.play-latino.com:27702/api/v2"
  }
}
```

This tells the middleware where to call for token refresh.

## JWT Configuration

### Required Settings

```json
{
  "Jwt": {
    "Issuer": "ticolinea.panel",
    "Audience": "streaming-node",
    "PublicKey": "RSA public key in PEM format",
    "NodeProviderId": "main",
    "PanelApiUrl": "http://tv.play-latino.com:27702/api/v2",
    "AccessTokenExpiryMinutes": 5,
    "RefreshTokenExpiryDays": 364
  }
}
```

### Public Key

The **RSA public key** must match the **private key** used by the Panel API to sign tokens.

**Format:**
```
-----BEGIN PUBLIC KEY-----
MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEA...
-----END PUBLIC KEY-----
```

In JSON config, use `\n` for line breaks:
```json
"PublicKey": "-----BEGIN PUBLIC KEY-----\nMIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEA...\n-----END PUBLIC KEY-----"
```

## Token Validation Implementation

### TokenValidation Helper

Located in: `Helpers/TokenValidation.cs`

**Key Methods:**
- `ValidateToken()`: Validates access token and extracts claims
- `ValidateRefreshToken()`: Validates refresh token signature
- `RefreshTokensFromPanel()`: Calls Panel API to refresh tokens
- `ExtractToken()`: Extracts token from request headers/query

**Initialization:**
Called from `Program.cs`:
```csharp
TokenValidation.Initialize(jwtSettings);
```

### AuthController

Located in: `Controllers/AuthController.cs`

**Endpoints:**
- `POST /Auth/Refresh`: Refresh access token
- `GET /Auth/Validate`: Validate token and return claims
- `GET /Auth/Status`: Check if token is valid/needs refresh

## Stream Access Authorization

### PlaylistByToken Endpoint

**Endpoint:** `GET /Streams/PlaylistByToken/PlaylistByToken?token=...`

**Process:**
1. Extracts token from query parameter
2. Validates token using `TokenValidation.ValidateToken()`
3. Checks provider ID matches this node
4. Filters streams based on user's `packageIds`
5. Returns HLS playlist

### Token Requirements

For stream access, tokens must have:
- Valid signature (signed by Panel API)
- Not expired
- `providerId` matches `NodeProviderId`
- `packageIds` claim with user's packages
- `token_type` = "access" (not "refresh")

## Security Features

1. **Signature Validation**: All tokens are cryptographically verified
2. **Expiration Checking**: Access tokens expire in 5 minutes
3. **Provider Isolation**: Only tokens for this provider are accepted
4. **User Status Check**: Refresh requires Panel API to verify user is active
5. **Token Type Validation**: Refresh tokens cannot be used as access tokens

## Error Handling

Common validation errors:

- `invalid_or_expired_token`: Token signature invalid or expired
- `refresh_token_required`: Refresh token missing in request
- `invalid_refresh_token`: Refresh token signature invalid
- `refresh_failed`: Panel API rejected refresh (user inactive/deleted)
- `no_access_token`: Panel API didn't return access token

## Network Communication

### Panel API Call

When refreshing tokens, the middleware makes an HTTP POST request:

```csharp
POST {PanelApiUrl}/auth/refresh
Content-Type: application/json

{
  "refreshToken": "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9..."
}
```

**Timeout:** 10 seconds  
**Error Handling:** Returns null if Panel API is unreachable or returns error

### Fallback Behavior

If Panel API is unreachable:
- Refresh request fails
- Client must re-login
- No cached tokens are used (security)

## Configuration Files

### appsettings.json (Base)
Contains default JWT settings and public key placeholder.

### appsettings.main.json (Provider-specific)
Overrides base settings with provider-specific values:
- `NodeProviderId`: "main"
- `PanelApiUrl`: Panel API base URL for refresh calls

### appsettings.Production.json (Environment-specific)
Can override settings for production environment.

## Troubleshooting

### Token Validation Fails

1. **Check public key** matches Panel API's private key
2. **Verify issuer/audience** match Panel API settings
3. **Check token expiration** (access tokens expire in 5 min)
4. **Verify provider ID** matches `NodeProviderId`

### Refresh Fails

1. **Check Panel API URL** is correct and reachable
2. **Verify network connectivity** to Panel API
3. **Check Panel API logs** for refresh endpoint errors
4. **Verify user is still active** in Panel database

### Common Issues

- **"invalid_token"**: Public key doesn't match private key
- **"token_expired"**: Access token expired (normal, use refresh)
- **"refresh_failed"**: User was deactivated or Panel API unreachable
- **"provider mismatch"**: Token is for different provider node

