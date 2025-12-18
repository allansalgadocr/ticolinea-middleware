using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using ticolinea.stream.service.Config;

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
            _publicKey = LoadPublicKey(settings.PublicKey);
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

                // Validate providerId matches this node
                if (!string.IsNullOrEmpty(_settings.NodeProviderId) &&
                    !string.Equals(result.ProviderId, _settings.NodeProviderId, StringComparison.OrdinalIgnoreCase))
                {
                    return null; // Token is for a different provider node
                }

                return result;
            }
            catch (SecurityTokenExpiredException)
            {
                return null; // Token expired
            }
            catch (SecurityTokenException)
            {
                return null; // Invalid token
            }
            catch (Exception)
            {
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
                return queryToken;

            // Try Authorization header
            var authHeader = request.Headers["Authorization"].FirstOrDefault();
            if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                return authHeader.Substring(7).Trim();

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
                return null;

            try
            {
                var rsa = RSA.Create();
                
                // Remove PEM headers and whitespace
                var keyContent = publicKeyPem
                    .Replace("-----BEGIN PUBLIC KEY-----", "")
                    .Replace("-----END PUBLIC KEY-----", "")
                    .Replace("\n", "")
                    .Replace("\r", "")
                    .Trim();

                var keyBytes = Convert.FromBase64String(keyContent);
                rsa.ImportSubjectPublicKeyInfo(keyBytes, out _);
                
                return new RsaSecurityKey(rsa);
            }
            catch
            {
                return null;
            }
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
        public string? Error { get; set; }
    }
}
