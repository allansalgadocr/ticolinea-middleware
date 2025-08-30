using System.Collections.Concurrent;
using ticolinea.stream.service.Modelos;

namespace ticolinea.stream.service.Helpers
{
    public static class StreamDataCache
    {
        private static readonly ConcurrentDictionary<int, StreamDb> _activeStreamsCache = new();
        private static readonly object _cacheLock = new object();
        private static DateTime _lastCacheRefresh = DateTime.MinValue;
        private static readonly TimeSpan _cacheExpiration = TimeSpan.FromSeconds(30); // 30 seconds cache

        public static bool IsCacheValid()
        {
            return DateTime.UtcNow - _lastCacheRefresh < _cacheExpiration;
        }

        public static void UpdateCache(List<StreamDb> streams)
        {
            lock (_cacheLock)
            {
                _activeStreamsCache.Clear();
                foreach (var stream in streams)
                {
                    _activeStreamsCache.TryAdd(stream.StreamId, stream);
                }
                _lastCacheRefresh = DateTime.UtcNow;
            }
        }

        public static List<StreamDb> GetCachedStreams()
        {
            lock (_cacheLock)
            {
                return _activeStreamsCache.Values.ToList();
            }
        }

        public static void InvalidateCache()
        {
            lock (_cacheLock)
            {
                _activeStreamsCache.Clear();
                _lastCacheRefresh = DateTime.MinValue;
            }
        }

        public static void UpdateStreamInCache(StreamDb stream)
        {
            lock (_cacheLock)
            {
                _activeStreamsCache.AddOrUpdate(stream.StreamId, stream, (key, oldValue) => stream);
            }
        }

        public static void RemoveStreamFromCache(int streamId)
        {
            lock (_cacheLock)
            {
                _activeStreamsCache.TryRemove(streamId, out _);
            }
        }

        public static StreamDb? GetStreamFromCache(int streamId)
        {
            lock (_cacheLock)
            {
                return _activeStreamsCache.TryGetValue(streamId, out var stream) ? stream : null;
            }
        }

        public static int GetCacheSize()
        {
            lock (_cacheLock)
            {
                return _activeStreamsCache.Count;
            }
        }

        public static DateTime GetLastRefreshTime()
        {
            lock (_cacheLock)
            {
                return _lastCacheRefresh;
            }
        }
    }
}
