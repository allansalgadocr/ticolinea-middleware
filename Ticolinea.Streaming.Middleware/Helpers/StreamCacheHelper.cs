using Microsoft.Extensions.Caching.Memory;
using ticolinea.stream.service.Modelos;

namespace ticolinea.stream.service.Helpers
{
    public static class StreamCacheHelper
    {
        private static readonly MemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
        private static readonly TimeSpan _cacheExpiration = TimeSpan.FromMinutes(5);

        public static StreamDb? GetCachedStream(int streamId)
        {
            var cacheKey = $"stream_config_{streamId}";
            return _cache.TryGetValue(cacheKey, out StreamDb? cachedStream) ? cachedStream : null;
        }

        public static void SetCachedStream(int streamId, StreamDb stream)
        {
            var cacheKey = $"stream_config_{streamId}";
            _cache.Set(cacheKey, stream, _cacheExpiration);
        }

        public static void InvalidateStream(int streamId)
        {
            var cacheKey = $"stream_config_{streamId}";
            _cache.Remove(cacheKey);
        }
    }
}
