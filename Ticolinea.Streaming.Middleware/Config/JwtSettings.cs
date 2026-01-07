namespace ticolinea.stream.service.Config
{
    public class JwtSettings
    {
        public const string SectionName = "Jwt";
        
        /// <summary>
        /// Expected issuer (ticolinea.panel)
        /// </summary>
        public string Issuer { get; set; } = string.Empty;
        
        /// <summary>
        /// Expected audience for streaming nodes
        /// </summary>
        public string Audience { get; set; } = string.Empty;
        
        /// <summary>
        /// RSA public key in PEM format for signature validation
        /// </summary>
        public string PublicKey { get; set; } = string.Empty;
        
        /// <summary>
        /// Provider ID that this node serves (must match token's providerId claim)
        /// </summary>
        public string NodeProviderId { get; set; } = string.Empty;
        
        /// <summary>
        /// Optional: Panel introspection/refresh endpoint base URL
        /// </summary>
        public string? PanelApiUrl { get; set; }
        
        /// <summary>
        /// API Key for authenticating with Panel API (X-Auth-API-Key header)
        /// </summary>
        public string? PanelApiKey { get; set; }
        
        /// <summary>
        /// Cache duration in seconds for introspection results (default 60s)
        /// </summary>
        public int IntrospectCacheSeconds { get; set; } = 60;
        
        /// <summary>
        /// Access token expiry in minutes (default 60)
        /// </summary>
        public int AccessTokenExpiryMinutes { get; set; } = 60;
        
        /// <summary>
        /// Refresh token expiry in days (default 30)
        /// </summary>
        public int RefreshTokenExpiryDays { get; set; } = 30;
    }
}
