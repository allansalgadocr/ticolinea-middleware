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
        /// Identidad compacta y segura de un token para logs de RECHAZO: los claims
        /// que identifican al cliente (sub/provider/mac/jti/exp), nunca el token
        /// crudo — un JWT en el log es una credencial filtrada. Parsea SIN validar:
        /// para diagnosticar un rechazo hay que saber QUIÉN lo intentó, no confirmar
        /// su firma. Nunca lanza: un token ilegible devuelve su forma (largo/puntos)
        /// para distinguir "JWT malformado" de "esto no era un JWT".
        /// </summary>
        public static string DescribeForLog(string? token)
        {
            if (string.IsNullOrWhiteSpace(token)) return "token=(empty)";
            try
            {
                var handler = new JwtSecurityTokenHandler();
                if (handler.CanReadToken(token))
                {
                    var jwt = handler.ReadJwtToken(token);
                    string Claim(string t) => jwt.Claims.FirstOrDefault(c => c.Type == t)?.Value ?? "-";
                    return $"sub={Claim("sub")} provider={Claim("providerId")} mac={Claim("mac")} jti={Claim("jti")} exp={jwt.ValidTo:yyyy-MM-dd'T'HH:mm:ss}Z";
                }
            }
            catch { /* cae a la línea de forma */ }
            return $"token=(unreadable len={token.Length} dots={token.Count(c => c == '.')})";
        }

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

            // Éxito = SILENCIO. Las 7 líneas por request que vivían aquí (preview del
            // token incluido — una credencial parcial en el log) llenaban syslog a
            // ~7.5GB/día en main sin aportar nada: un token que valida no deja nada
            // que diagnosticar. El detalle completo vive ahora en los caminos de
            // RECHAZO, con DescribeForLog identificando al cliente. (Fix: 401s
            // indiagnosticables del 2026-07-30 — sabíamos cuándo fallaba un token
            // pero nunca de quién.)
            try
            {
                var tokenHandler = new JwtSecurityTokenHandler();

                if (!tokenHandler.CanReadToken(token))
                {
                    Console.WriteLine($"[TokenValidation] Rechazado: no es un JWT legible ({DescribeForLog(token)})");
                    return null;
                }

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
                {
                    // Rechazo silencioso hasta 2026-07-30: un cliente usando su refresh
                    // token como access token veía 401 sin rastro alguno en el log.
                    Console.WriteLine($"[TokenValidation] Rechazado: refresh token usado como access token ({DescribeForLog(token)})");
                    return null; // Refresh tokens cannot be used as access tokens
                }

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
                Console.WriteLine($"[TokenValidation] Token expired ({DescribeForLog(token)}): {ex.Message}");
                return null; // Token expired
            }
            catch (SecurityTokenInvalidSignatureException ex)
            {
                Console.WriteLine($"[TokenValidation] Invalid signature ({DescribeForLog(token)}) - Key mismatch? Issuer: {_settings?.Issuer}, Audience: {_settings?.Audience}: {ex.Message}");
                return null; // Invalid signature - likely key mismatch
            }
            catch (SecurityTokenInvalidIssuerException ex)
            {
                Console.WriteLine($"[TokenValidation] Invalid issuer ({DescribeForLog(token)}). Expected: {_settings?.Issuer}, Got: {ex.InvalidIssuer}");
                return null; // Invalid issuer
            }
            catch (SecurityTokenInvalidAudienceException ex)
            {
                var invalidAudiences = ex.InvalidAudience != null ? string.Join(", ", ex.InvalidAudience) : "(none)";
                Console.WriteLine($"[TokenValidation] Invalid audience ({DescribeForLog(token)}). Expected: {_settings?.Audience}, Got: {invalidAudiences}");
                return null; // Invalid audience
            }
            catch (SecurityTokenException ex)
            {
                Console.WriteLine($"[TokenValidation] Security token exception ({DescribeForLog(token)}): {ex.GetType().Name} - {ex.Message}");
                return null; // Invalid token
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TokenValidation] Unexpected exception ({DescribeForLog(token)}): {ex.GetType().Name} - {ex.Message}");
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
            // Try query parameter first. Éxito en silencio — el camino feliz corría
            // por cada request de cada dispositivo y era relleno puro de syslog.
            if (request.Query.TryGetValue("token", out var queryToken) && !string.IsNullOrEmpty(queryToken))
            {
                // URL decode the token in case it's encoded
                return WebUtility.UrlDecode(queryToken.ToString());
            }

            // Try Authorization header
            var authHeader = request.Headers["Authorization"].FirstOrDefault();
            if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                return authHeader.Substring(7).Trim();
            }

            // Este sí se queda: "sin token" es un rechazo inminente y la única pista
            // de si el cliente intentó query, header, o nada.
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
