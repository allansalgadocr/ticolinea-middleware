namespace ticolinea.stream.service.Helpers
{
    /// <summary>
    /// Resolves which package's streams to serve for a client, by priority.
    /// Shared by both the JWT-token and the credential/MAC playlist paths.
    /// </summary>
    public static class PackageResolver
    {
        /// <summary>
        /// Resolves the package id to filter streams by, applying the priority:
        ///   external provider        -> "" (ALL streams, never filtered)
        ///   1. client package        -> the client's own package
        ///   2. provider package      -> the provider's default package
        ///   3. fallback              -> "" (ALL streams)
        /// An empty/whitespace return means "serve all enabled live streams"
        /// (the existing behavior of ObtenerCanales*Async("")).
        /// </summary>
        public static string ResolvePaqueteTvId(
            bool isExternal,
            string? clientPackageId,
            string? providerPackageId)
        {
            if (isExternal)
                return string.Empty;

            if (!string.IsNullOrWhiteSpace(clientPackageId))
                return clientPackageId.Trim();

            if (!string.IsNullOrWhiteSpace(providerPackageId))
                return providerPackageId.Trim();

            return string.Empty;
        }
    }
}
