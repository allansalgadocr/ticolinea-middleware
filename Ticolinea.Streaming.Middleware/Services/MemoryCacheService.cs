using Microsoft.Extensions.Caching.Memory;
using System;

namespace ticolinea.stream.service.Services
{
    public class MemoryCacheService : IDisposable
    {
        private readonly MemoryCache _memoryCache;
        private readonly object _cacheLock = new object();

        public MemoryCacheService()
        {
            var cacheOptions = new MemoryCacheOptions
            {
                SizeLimit = 10000 // Set a reasonable size limit to avoid excessive memory consumption
            };
            _memoryCache = new MemoryCache(cacheOptions);
        }

        public void GuardarEnCache(string key, object objeto, int minutos)
        {
            var cacheEntryOptions = new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(minutos),
                Size = 1000
            };

            lock (_cacheLock)
            {
                _memoryCache.Set(key, objeto, cacheEntryOptions);
            }
        }

        public bool ExisteDatoEnCache(string key)
        {
            return _memoryCache.TryGetValue(key, out _);
        }

        public T ObtenerDatoEnCache<T>(string key) where T : class
        {
            lock (_cacheLock)
            {
                if (_memoryCache.TryGetValue(key, out var value))
                {
                    return value as T;
                }
                return null;
            }
        }

        public void EliminarDatoEnCache(string key)
        {
            lock (_cacheLock)
            {
                _memoryCache.Remove(key);
            }
        }

        public void Dispose()
        {
            _memoryCache.Dispose();
        }
    }
}
