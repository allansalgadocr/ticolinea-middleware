namespace ticolinea.stream.service.Helpers
{
    /// <summary>
    /// Decisiones puras sobre argumentos de entrada de FFmpeg, extraídas para poder
    /// probarlas sin levantar el loop de supervisión de StreamingService.
    /// </summary>
    public static class FfmpegInputPolicy
    {
        /// <summary>
        /// Decide si se agrega "-rw_timeout" para la fuente de un stream.
        /// - Token explícito "rw_timeout" en los parámetros: siempre activo (comportamiento existente).
        /// - Fuentes http/https: activo POR DEFECTO, salvo opt-out con el token "no_rw_timeout".
        ///   Razón: una fuente HTTP colgada deja a FFmpeg vivo-pero-atascado para siempre con un
        ///   PID válido, y la supervisión basada en PID nunca lo detecta. Con el timeout la
        ///   lectura falla, FFmpeg sale, y la supervisión existente (retry/backoff/circuit
        ///   breaker) reinicia el stream — auto-recuperación sin watchdog aparte.
        /// - Otras fuentes (rtmp, udp, archivos, ...): inactivo salvo opt-in, sin cambios.
        /// El caller solo invoca esto para fuentes no-SRT; SRT nunca lleva el flag.
        /// </summary>
        public static bool ShouldApplyRwTimeout(string fuente, string[] parameters)
        {
            if (parameters.Contains("rw_timeout"))
                return true;

            var isHttp = fuente.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                         || fuente.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

            return isHttp && !parameters.Contains("no_rw_timeout");
        }
    }
}
