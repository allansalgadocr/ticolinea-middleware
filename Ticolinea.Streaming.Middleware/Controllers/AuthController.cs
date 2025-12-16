using Microsoft.AspNetCore.Mvc;
using ticolinea.stream.service.Helpers;

namespace ticolinea.stream.service.Controllers
{
    [Route("[controller]/[action]")]
    [ApiController]
    public class AuthController : ControllerBase
    {
        /// <summary>
        /// Refresh access token using refresh token.
        /// This endpoint proxies to the panel API which checks if user is still active.
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> Refresh([FromBody] RefreshRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.RefreshToken))
                return BadRequest(new { error = "refresh_token_required" });

            // Validate refresh token signature locally first
            var localValidation = TokenValidation.ValidateRefreshToken(request.RefreshToken);
            if (localValidation == null || !localValidation.IsValid)
                return Unauthorized(new { error = "invalid_refresh_token" });

            // Call panel API to get new tokens (panel checks if user is active/not deleted)
            var refreshResponse = await TokenValidation.RefreshTokensFromPanel(request.RefreshToken);
            
            if (refreshResponse == null)
                return Unauthorized(new { error = "refresh_failed", message = "Unable to refresh token. User may be inactive or deleted." });

            if (!string.IsNullOrEmpty(refreshResponse.Error))
                return Unauthorized(new { error = refreshResponse.Error });

            if (string.IsNullOrEmpty(refreshResponse.AccessToken))
                return Unauthorized(new { error = "no_access_token" });

            return Ok(new
            {
                access_token = refreshResponse.AccessToken,
                refresh_token = refreshResponse.RefreshToken ?? request.RefreshToken, // Return new refresh token if rotated
                expires_in = refreshResponse.ExpiresIn,
                token_type = "Bearer"
            });
        }

        /// <summary>
        /// Validate access token - returns token info if valid
        /// </summary>
        [HttpGet]
        public IActionResult Validate([FromQuery] string? token = null)
        {
            var extractedToken = token ?? TokenValidation.ExtractToken(Request);
            var validation = TokenValidation.ValidateToken(extractedToken);

            if (validation == null || !validation.IsValid)
                return Unauthorized(new { valid = false, error = "invalid_or_expired_token" });

            return Ok(new
            {
                valid = true,
                sub = validation.Sub,
                providerId = validation.ProviderId,
                packageIds = validation.PackageIds,
                moviesAllowed = validation.MoviesAllowed,
                mac = validation.Mac
            });
        }

        /// <summary>
        /// Check token expiry status without full validation
        /// Useful for clients to decide if they need to refresh
        /// </summary>
        [HttpGet]
        public IActionResult Status([FromQuery] string? token = null)
        {
            var extractedToken = token ?? TokenValidation.ExtractToken(Request);
            
            if (string.IsNullOrWhiteSpace(extractedToken))
                return BadRequest(new { error = "token_required" });

            var validation = TokenValidation.ValidateToken(extractedToken);
            
            return Ok(new
            {
                valid = validation?.IsValid ?? false,
                needsRefresh = validation == null || !validation.IsValid
            });
        }
    }

    public class RefreshRequest
    {
        public string? RefreshToken { get; set; }
    }
}

