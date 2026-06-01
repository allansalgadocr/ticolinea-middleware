using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using ticolinea.stream.service.Config;
using System.Net;

namespace ticolinea.stream.service.Helpers
{
    public class TokenValidation
    {
        private static JwtSettings? _settings;
        private static RsaSecurityKey? _publicKey;
        private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(10) };

        /// <summary>
        /// Initialize with JWT settings (call from Program.cs)
        /// </summary>
        public static void Initialize(JwtSettings settings)
        {
            _settings = settings;
            
            // Ensure issuer and audience are trimmed (no whitespace)
            if (_settings != null)
            {
                _settings.Issuer = _settings.Issuer?.Trim() ?? string.Empty;
                _settings.Audience = _settings.Audience?.Trim() ?? string.Empty;
            }
            
            _publicKey = LoadPublicKey(settings.PublicKey);
            
            if (_publicKey == null)
            {
                Console.WriteLine("[TokenValidation] WARNING: Public key failed to load!");
            }
            else
            {
                Console.WriteLine($"[TokenValidation] Public key loaded successfully.");
                Console.WriteLine($"[TokenValidation] Configured Issuer: '{_settings?.Issuer}' (length: {_settings?.Issuer?.Length ?? 0})");
                Console.WriteLine($"[TokenValidation] Configured Audience: '{_settings?.Audience}' (length: {_settings?.Audience?.Length ?? 0})");
            }
        }

        /// <summary>
        /// Get current settings
        /// </summary>
        public static JwtSettings? GetSettings() => _settings;

        /// <summary>
        /// Validates a JWT access token and returns claims if valid
        /// </summary>
        public static TokenValidationResult? ValidateToken(string? token)
        {
            if (string.IsNullOrWhiteSpace(token) || _settings == null || _publicKey == null)
            {
                Console.WriteLine($"[TokenValidation] ValidateToken called with invalid parameters. Token: {!string.IsNullOrWhiteSpace(token)}, Settings: {_settings != null}, PublicKey: {_publicKey != null}");
                return null;
            }

            // Log token info for debugging (first and last 50 chars, length)
            var tokenPreview = token.Length > 100 
                ? $"{token.Substring(0, 50)}...{token.Substring(token.Length - 50)}" 
                : token;
            Console.WriteLine($"[TokenValidation] Token received - Length: {token.Length}, Preview: {tokenPreview}");
            Console.WriteLine($"[TokenValidation] Token starts with: {(token.Length > 20 ? token.Substring(0, 20) : token)}");

            try
            {
                // First, try to read the token without validation to see its claims
                var tokenHandler = new JwtSecurityTokenHandler();
                
                if (!tokenHandler.CanReadToken(token))
                {
                    Console.WriteLine($"[TokenValidation] Token cannot be read - may be malformed or not a JWT");
                    Console.WriteLine($"[TokenValidation] Token format check - Contains dots: {token.Contains('.')}, Dot count: {token.Count(c => c == '.')}");
                    // JWT should have 3 parts separated by dots: header.payload.signature
                    var parts = token.Split('.');
                    Console.WriteLine($"[TokenValidation] Token parts count: {parts.Length} (expected 3)");
                    if (parts.Length > 0)
                    {
                        Console.WriteLine($"[TokenValidation] First part (header) length: {parts[0].Length}");
                    }
                    return null;
                }
                
                var unvalidatedToken = tokenHandler.ReadJwtToken(token);
                var tokenIssuer = unvalidatedToken.Issuer ?? "(null)";
                var tokenAudiences = unvalidatedToken.Audiences != null ? string.Join(", ", unvalidatedToken.Audiences) : "(null)";
                
                Console.WriteLine($"[TokenValidation] Token issuer: '{tokenIssuer}' (length: {tokenIssuer.Length})");
                Console.WriteLine($"[TokenValidation] Token audiences: '{tokenAudiences}'");
                Console.WriteLine($"[TokenValidation] Expected issuer: '{_settings.Issuer}' (length: {_settings.Issuer?.Length ?? 0})");
                Console.WriteLine($"[TokenValidation] Expected audience: '{_settings.Audience}' (length: {_settings.Audience?.Length ?? 0})");
                
                // Check for exact match (case-sensitive)
                var issuerMatch = string.Equals(tokenIssuer, _settings.Issuer, StringComparison.Ordinal);
                var audienceMatch = unvalidatedToken.Audiences != null && unvalidatedToken.Audiences.Contains(_settings.Audience, StringComparer.Ordinal);
                
                Console.WriteLine($"[TokenValidation] Issuer match: {issuerMatch}, Audience match: {audienceMatch}");

                var validationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = _settings.Issuer,
                    ValidateAudience = true,
                    ValidAudience = _settings.Audience,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = _publicKey,
                    ClockSkew = TimeSpan.FromMinutes(1)
                };

                var principal = tokenHandler.ValidateToken(token, validationParameters, out var validatedToken);

                if (validatedToken is not JwtSecurityToken jwtToken)
                    return null;

                // Check token type - must be access token
                var tokenType = GetClaimValue(principal, "token_type");
                if (tokenType == "refresh")
                    return null; // Refresh tokens cannot be used as access tokens

                // Extract claims
                var result = new TokenValidationResult
                {
                    IsValid = true,
                    Sub = GetClaimValue(principal, "sub") ?? GetClaimValue(principal, ClaimTypes.NameIdentifier) ?? "",
                    ProviderId = GetClaimValue(principal, "providerId") ?? "",
                    ProviderUrl = GetClaimValue(principal, "providerUrl") ?? "",
                    Mac = GetClaimValue(principal, "mac"),
                    Jti = GetClaimValue(principal, "jti") ?? "",
                    MoviesAllowed = GetClaimValue(principal, "moviesAllowed")?.ToLower() == "true",
                    IsExternal = GetClaimValue(principal, "isExternal")?.ToLower() == "true",
                    ProviderPackageId = GetClaimValue(principal, "providerPackageId") ?? "",
                    Token = token
                };

                // Parse packageIds (comma-separated or JSON array)
                var packageIdsRaw = GetClaimValue(principal, "packageIds");
                if (!string.IsNullOrEmpty(packageIdsRaw))
                {
                    result.PackageIds = packageIdsRaw
                        .Trim('[', ']', '"')
                        .Split(new[] { ',', '"' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => s.Trim())
                        .Where(s => !string.IsNullOrEmpty(s))
                        .ToList();
                }

                // Skip providerId validation - accept tokens regardless of providerId
                // This allows tokens from Panel API to work on any streaming node
                // ProviderId is informational only, not used for access control

                return result;
            }
            catch (SecurityTokenExpiredException ex)
            {
                Console.WriteLine($"[TokenValidation] Token expired: {ex.Message}");
                return null; // Token expired
            }
            catch (SecurityTokenInvalidSignatureException ex)
            {
                Console.WriteLine($"[TokenValidation] Invalid signature - Key mismatch? Issuer: {_settings?.Issuer}, Audience: {_settings?.Audience}");
                Console.WriteLine($"[TokenValidation] Signature error details: {ex.Message}");
                return null; // Invalid signature - likely key mismatch
            }
            catch (SecurityTokenInvalidIssuerException ex)
            {
                Console.WriteLine($"[TokenValidation] Invalid issuer. Expected: {_settings?.Issuer}, Got: {ex.InvalidIssuer}");
                return null; // Invalid issuer
            }
            catch (SecurityTokenInvalidAudienceException ex)
            {
                var invalidAudiences = ex.InvalidAudience != null ? string.Join(", ", ex.InvalidAudience) : "(none)";
                Console.WriteLine($"[TokenValidation] Invalid audience. Expected: {_settings?.Audience}, Got: {invalidAudiences}");
                return null; // Invalid audience
            }
            catch (SecurityTokenException ex)
            {
                Console.WriteLine($"[TokenValidation] Security token exception: {ex.GetType().Name} - {ex.Message}");
                return null; // Invalid token
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TokenValidation] Unexpected exception: {ex.GetType().Name} - {ex.Message}");
                Console.WriteLine($"[TokenValidation] Stack trace: {ex.StackTrace}");
                return null;
            }
        }

        /// <summary>
        /// Validates a refresh token (does NOT check expiry strictly - panel will do that)
        /// Returns claims if signature is valid
        /// </summary>
        public static RefreshTokenValidationResult? ValidateRefreshToken(string? refreshToken)
        {
            if (string.IsNullOrWhiteSpace(refreshToken) || _settings == null || _publicKey == null)
                return null;

            try
            {
                var tokenHandler = new JwtSecurityTokenHandler();
                var validationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = _settings.Issuer,
                    ValidateAudience = true,
                    ValidAudience = _settings.Audience,
                    ValidateLifetime = true, // Check expiry
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = _publicKey,
                    ClockSkew = TimeSpan.FromMinutes(1)
                };

                var principal = tokenHandler.ValidateToken(refreshToken, validationParameters, out var validatedToken);

                if (validatedToken is not JwtSecurityToken jwtToken)
                    return null;

                // Check token type - must be refresh token
                var tokenType = GetClaimValue(principal, "token_type");
                if (tokenType != "refresh")
                    return null; // Not a refresh token

                return new RefreshTokenValidationResult
                {
                    IsValid = true,
                    Sub = GetClaimValue(principal, "sub") ?? GetClaimValue(principal, ClaimTypes.NameIdentifier) ?? "",
                    ProviderId = GetClaimValue(principal, "providerId") ?? "",
                    Jti = GetClaimValue(principal, "jti") ?? "",
                    RefreshToken = refreshToken
                };
            }
            catch (SecurityTokenExpiredException)
            {
                return null; // Refresh token expired - user must re-login
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Check if user account is still active by calling Panel API
        /// </summary>
        public static async Task<bool> CheckUserStatusFromPanel(string accessToken)
        {
            if (_settings == null || string.IsNullOrEmpty(_settings.PanelApiUrl))
                return false;

            try
            {
                // Call Panel API status endpoint to check if user is still active
                var statusUrl = $"{_settings.PanelApiUrl.TrimEnd('/')}/auth/status";
                var request = new HttpRequestMessage(HttpMethod.Post, statusUrl);
                request.Content = new StringContent(
                    JsonSerializer.Serialize(new { accessToken }),
                    System.Text.Encoding.UTF8,
                    "application/json"
                );
                
                // Add API key header if configured
                if (!string.IsNullOrEmpty(_settings.PanelApiKey))
                {
                    request.Headers.Add("X-Auth-API-Key", _settings.PanelApiKey);
                }

                var response = await _httpClient.SendAsync(request);
                
                if (!response.IsSuccessStatusCode)
                    return false;

                var content = await response.Content.ReadAsStringAsync();
                var statusResponse = JsonSerializer.Deserialize<StatusResponse>(content, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                return statusResponse?.Valid ?? false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Call panel API to refresh tokens - checks user is still active
        /// </summary>
        public static async Task<RefreshResponse?> RefreshTokensFromPanel(string refreshToken)
        {
            if (_settings == null || string.IsNullOrEmpty(_settings.PanelApiUrl))
                return null;

            try
            {
                var refreshUrl = $"{_settings.PanelApiUrl.TrimEnd('/')}/auth/refresh";
                var request = new HttpRequestMessage(HttpMethod.Post, refreshUrl);
                request.Content = new StringContent(
                    JsonSerializer.Serialize(new { refreshToken }),
                    System.Text.Encoding.UTF8,
                    "application/json"
                );
                
                // Add API key header if configured
                if (!string.IsNullOrEmpty(_settings.PanelApiKey))
                {
                    request.Headers.Add("X-Auth-API-Key", _settings.PanelApiKey);
                }

                var response = await _httpClient.SendAsync(request);
                
                if (!response.IsSuccessStatusCode)
                    return null;

                var content = await response.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<RefreshResponse>(content, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Extract token from query string or Authorization header
        /// </summary>
        public static string? ExtractToken(HttpRequest request)
        {
            // Try query parameter first
            if (request.Query.TryGetValue("token", out var queryToken) && !string.IsNullOrEmpty(queryToken))
            {
                Console.WriteLine($"[TokenValidation] Token extracted from query parameter - Length: {queryToken.ToString().Length}");
                // URL decode the token in case it's encoded
                var decoded = WebUtility.UrlDecode(queryToken.ToString());
                if (decoded != queryToken.ToString())
                {
                    Console.WriteLine($"[TokenValidation] Token was URL encoded, decoded length: {decoded.Length}");
                }
                return decoded;
            }

            // Try Authorization header
            var authHeader = request.Headers["Authorization"].FirstOrDefault();
            if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                var token = authHeader.Substring(7).Trim();
                Console.WriteLine($"[TokenValidation] Token extracted from Authorization header - Length: {token.Length}");
                return token;
            }

            Console.WriteLine($"[TokenValidation] No token found in request - Query has 'token': {request.Query.ContainsKey("token")}, Auth header present: {!string.IsNullOrEmpty(authHeader)}");
            return null;
        }

        /// <summary>
        /// Extract refresh token from request body or header
        /// </summary>
        public static string? ExtractRefreshToken(HttpRequest request)
        {
            // Try X-Refresh-Token header
            var refreshHeader = request.Headers["X-Refresh-Token"].FirstOrDefault();
            if (!string.IsNullOrEmpty(refreshHeader))
                return refreshHeader;

            return null;
        }

        private static string? GetClaimValue(ClaimsPrincipal principal, string claimType)
        {
            return principal.FindFirst(claimType)?.Value;
        }

        private static RsaSecurityKey? LoadPublicKey(string publicKeyPem)
        {
            if (string.IsNullOrWhiteSpace(publicKeyPem))
            {
                Console.WriteLine("[TokenValidation] Public key is null or empty");
                return null;
            }

            try
            {
                var rsa = RSA.Create();
                
                // Remove all PEM headers (both PUBLIC and PRIVATE - in case of copy-paste error)
                // Also handle both \n and actual newlines
                var keyContent = publicKeyPem
                    .Replace("-----BEGIN PUBLIC KEY-----", "", StringComparison.OrdinalIgnoreCase)
                    .Replace("-----END PUBLIC KEY-----", "", StringComparison.OrdinalIgnoreCase)
                    .Replace("-----BEGIN PRIVATE KEY-----", "", StringComparison.OrdinalIgnoreCase)
                    .Replace("-----END PRIVATE KEY-----", "", StringComparison.OrdinalIgnoreCase)
                    .Replace("\\n", "")  // Handle escaped newlines in JSON
                    .Replace("\n", "")  // Handle actual newlines
                    .Replace("\r", "")  // Handle carriage returns
                    .Replace(" ", "")   // Remove any spaces
                    .Trim();

                if (string.IsNullOrWhiteSpace(keyContent))
                {
                    Console.WriteLine("[TokenValidation] Public key content is empty after removing headers");
                    return null;
                }

                // Log the first and last few characters for debugging (without exposing full key)
                Console.WriteLine($"[TokenValidation] Key content length: {keyContent.Length}, starts with: {keyContent.Substring(0, Math.Min(20, keyContent.Length))}...");

                var keyBytes = Convert.FromBase64String(keyContent);
                rsa.ImportSubjectPublicKeyInfo(keyBytes, out _);
                
                Console.WriteLine($"[TokenValidation] Public key loaded successfully. Key size: {rsa.KeySize} bits");
                return new RsaSecurityKey(rsa);
            }
            catch (FormatException ex)
            {
                Console.WriteLine($"[TokenValidation] Failed to load public key: Base64 format error - {ex.Message}");
                Console.WriteLine($"[TokenValidation] Key preview (first 100 chars): {publicKeyPem.Substring(0, Math.Min(100, publicKeyPem.Length))}...");
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TokenValidation] Failed to load public key: {ex.GetType().Name} - {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Normalize provider ID for comparison (case-insensitive, no spaces)
        /// </summary>
        private static string NormalizeProviderId(string providerId)
        {
            if (string.IsNullOrWhiteSpace(providerId))
                return providerId;

            return providerId
                .ToLowerInvariant()
                .Replace(" ", "")
                .Replace("-", "")
                .Replace("_", "");
        }
    }

    public class TokenValidationResult
    {
        public bool IsValid { get; set; }
        
        /// <summary>
        /// Subject (user identifier)
        /// </summary>
        public string Sub { get; set; } = string.Empty;
        
        /// <summary>
        /// Provider ID this token is valid for
        /// </summary>
        public string ProviderId { get; set; } = string.Empty;
        
        /// <summary>
        /// Base URL for this provider's streaming node
        /// </summary>
        public string ProviderUrl { get; set; } = string.Empty;
        
        /// <summary>
        /// Package IDs the user has access to
        /// </summary>
        public List<string> PackageIds { get; set; } = new();
        
        /// <summary>
        /// First package ID (for compatibility with existing code)
        /// </summary>
        public string PaqueteTvId => PackageIds.FirstOrDefault() ?? "";
        
        /// <summary>
        /// Whether user can access movies/VOD
        /// </summary>
        public bool MoviesAllowed { get; set; }

        /// <summary>
        /// True if the provider is external — always serve all streams, never filter by package.
        /// </summary>
        public bool IsExternal { get; set; } = false;

        /// <summary>
        /// Provider's default package, used when the client has no package of their own.
        /// </summary>
        public string ProviderPackageId { get; set; } = string.Empty;

        /// <summary>
        /// MAC address binding (if any)
        /// </summary>
        public string? Mac { get; set; }
        
        /// <summary>
        /// JWT ID for revocation checking
        /// </summary>
        public string Jti { get; set; } = string.Empty;
        
        /// <summary>
        /// Original token (for passing to sub-requests)
        /// </summary>
        public string Token { get; set; } = string.Empty;
    }

    public class RefreshTokenValidationResult
    {
        public bool IsValid { get; set; }
        public string Sub { get; set; } = string.Empty;
        public string ProviderId { get; set; } = string.Empty;
        public string Jti { get; set; } = string.Empty;
        public string RefreshToken { get; set; } = string.Empty;
    }

    public class RefreshResponse
    {
        public string? AccessToken { get; set; }
        public string? RefreshToken { get; set; }
        public int ExpiresIn { get; set; }
        public string? ProviderUrl { get; set; }
        public string? Error { get; set; }
    }

    public class StatusResponse
    {
        public bool Valid { get; set; }
        public bool NeedsRefresh { get; set; }
        public string? Error { get; set; }
    }
}
