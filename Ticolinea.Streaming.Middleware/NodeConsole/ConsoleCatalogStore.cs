using MySqlConnector;

namespace ticolinea.stream.service.NodeConsole;

// Channel/category CRUD against the node's own catalog tables (streams_tl,
// stream_categories). Live channels only (tipo = 1) — VOD is out of scope.
public static class ConsoleCatalogStore
{
    private const int LiveType = 1;

    // ---------- categories ----------

    public static async Task<List<ConsoleCategory>> ListCategoriesAsync()
    {
        var list = new List<ConsoleCategory>();
        await using var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN);
        await cnn.OpenAsync();
        await using var cmd = cnn.CreateCommand();
        // LEFT JOIN so a category with no channels still appears — the owner
        // needs to see an empty category in order to fill or delete it.
        cmd.CommandText = @"
SELECT c.id, c.category_name, c.cat_order, COUNT(s.id)
FROM stream_categories c
LEFT JOIN streams_tl s ON s.id_categoria = c.id AND s.tipo = 1
GROUP BY c.id, c.category_name, c.cat_order
ORDER BY c.cat_order ASC, c.category_name ASC;";
        await using var r = (MySqlDataReader)await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(new ConsoleCategory
            {
                Id = r.GetInt32(0),
                Name = r.GetString(1),
                Order = r.GetInt32(2),
                ChannelCount = r.GetInt32(3),
            });
        return list;
    }

    public static async Task<ConsoleCategory> CreateCategoryAsync(string name)
    {
        await using var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN);
        await cnn.OpenAsync();
        await using var cmd = cnn.CreateCommand();
        // category_type 'live' mirrors what PanelController writes; the playlist
        // queries never filter on it, but staying consistent avoids surprises.
        cmd.CommandText = @"
INSERT INTO stream_categories (category_type, category_name, parent_id, cat_order)
VALUES ('live', @n, 0, (SELECT COALESCE(MAX(x.cat_order), 0) + 1 FROM stream_categories x));
SELECT LAST_INSERT_ID();";
        cmd.Parameters.AddWithValue("@n", name.Trim());
        var id = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        return new ConsoleCategory { Id = id, Name = name.Trim(), Order = 0, ChannelCount = 0 };
    }

    public static async Task<bool> RenameCategoryAsync(int id, string name)
    {
        await using var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN);
        await cnn.OpenAsync();
        await using var cmd = cnn.CreateCommand();
        cmd.CommandText = "UPDATE stream_categories SET category_name = @n WHERE id = @id;";
        cmd.Parameters.AddWithValue("@n", name.Trim());
        cmd.Parameters.AddWithValue("@id", id);
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public static async Task<bool> DeleteCategoryAsync(int id)
    {
        await using var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN);
        await cnn.OpenAsync();
        await using var tx = await cnn.BeginTransactionAsync();
        try
        {
            // Channels keep existing but lose their category. The playlist joins
            // categories with an INNER JOIN, so they drop out of the list until
            // reassigned — the UI states this count before confirming.
            await using (var orphan = cnn.CreateCommand())
            {
                orphan.Transaction = (MySqlTransaction)tx;
                orphan.CommandText = "UPDATE streams_tl SET id_categoria = NULL WHERE id_categoria = @id;";
                orphan.Parameters.AddWithValue("@id", id);
                await orphan.ExecuteNonQueryAsync();
            }

            int affected;
            await using (var del = cnn.CreateCommand())
            {
                del.Transaction = (MySqlTransaction)tx;
                del.CommandText = "DELETE FROM stream_categories WHERE id = @id;";
                del.Parameters.AddWithValue("@id", id);
                affected = await del.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
            return affected > 0;
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    // ---------- channels ----------

    public static async Task<List<ConsoleChannel>> ListChannelsAsync()
    {
        var list = new List<ConsoleChannel>();
        await using var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN);
        await cnn.OpenAsync();
        await using var cmd = cnn.CreateCommand();
        // No join to stream_categories: a channel whose category was deleted must
        // still be visible here, precisely so the owner can fix it.
        cmd.CommandText = @"
SELECT id, nombre_stream, fuente_stream, imagen_stream, id_categoria, orden, canal_epg, habilitado, sincronizado
FROM streams_tl
WHERE tipo = 1
ORDER BY orden ASC, id ASC;";
        await using var r = (MySqlDataReader)await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(new ConsoleChannel
            {
                Id = r.GetInt32(0),
                Name = r.IsDBNull(1) ? "" : r.GetString(1),
                Source = r.IsDBNull(2) ? "" : r.GetString(2),
                Logo = r.IsDBNull(3) ? "" : r.GetString(3),
                CategoryId = r.IsDBNull(4) ? null : r.GetInt32(4),
                Order = r.IsDBNull(5) ? 0 : r.GetInt32(5),
                EpgId = r.IsDBNull(6) ? "" : r.GetString(6),
                Enabled = !r.IsDBNull(7) && r.GetBoolean(7),
                Seeded = !r.IsDBNull(8) && r.GetBoolean(8),
            });
        return list;
    }

    public static async Task<ConsoleChannel> CreateChannelAsync(ChannelInput input)
    {
        await using var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN);
        await cnn.OpenAsync();
        await using var tx = await cnn.BeginTransactionAsync();
        try
        {
            int id;
            await using (var cmd = cnn.CreateCommand())
            {
                cmd.Transaction = (MySqlTransaction)tx;
                // sincronizado = 0 marks this as node-local: a future catalog sync
                // is contractually forbidden from touching these rows.
                // Playback defaults mirror PanelController's insert path so a
                // console-created channel behaves like a panel-created one.
                cmd.CommandText = @"
INSERT INTO streams_tl
  (nombre_stream, fuente_stream, imagen_stream, id_categoria, orden, agregado,
   probesize_ondemand, es_bajodemanda, tipo, contenedor, habilitado, transcode_audio,
   intervalo, segmentos, framerate, transcode, resolucion, bitrate, canal_epg,
   cgop, gop, canal_id, sincronizado)
VALUES
  (@n, @f, @img, @cat, (SELECT COALESCE(MAX(x.orden), 0) + 1 FROM streams_tl x), @added,
   512000, 0, 1, '', @hab, 'aac',
   6, 5, 25, 0, '', '1500k', @epg,
   0, 0, 0, 0);
SELECT LAST_INSERT_ID();";
                cmd.Parameters.AddWithValue("@n", input.Name!.Trim());
                cmd.Parameters.AddWithValue("@f", input.Source!.Trim());
                cmd.Parameters.AddWithValue("@img", input.Logo?.Trim() ?? "");
                cmd.Parameters.AddWithValue("@cat", (object?)input.CategoryId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@added", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                cmd.Parameters.AddWithValue("@hab", input.Enabled);
                cmd.Parameters.AddWithValue("@epg", input.EpgId?.Trim() ?? "");
                id = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            }

            // Without a streams_info row the supervision query
            // (streams_tl INNER JOIN streams_info ON habilitado=1 AND iniciado=1)
            // never sees the channel and FFmpeg never starts it.
            await using (var info = cnn.CreateCommand())
            {
                info.Transaction = (MySqlTransaction)tx;
                info.CommandText = @"
INSERT INTO streams_info (stream_id, ejecutando, proceso_id, info_progreso, iniciado, reportado_caido)
SELECT @id, 0, -1, '', 1, 0
WHERE NOT EXISTS (SELECT 1 FROM streams_info WHERE stream_id = @id);";
                info.Parameters.AddWithValue("@id", id);
                await info.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
            return new ConsoleChannel
            {
                Id = id,
                Name = input.Name!.Trim(),
                Source = input.Source!.Trim(),
                Logo = input.Logo?.Trim() ?? "",
                CategoryId = input.CategoryId,
                EpgId = input.EpgId?.Trim() ?? "",
                Enabled = input.Enabled,
                Seeded = false,
            };
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    /// <summary>Returns the previous source when the update changed it, so the caller can bounce the stream.</summary>
    public static async Task<(bool Found, string? PreviousSource)> UpdateChannelAsync(int id, ChannelInput input)
    {
        await using var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN);
        await cnn.OpenAsync();

        string? previous;
        await using (var read = cnn.CreateCommand())
        {
            read.CommandText = "SELECT fuente_stream FROM streams_tl WHERE id = @id AND tipo = 1;";
            read.Parameters.AddWithValue("@id", id);
            var scalar = await read.ExecuteScalarAsync();
            if (scalar == null) return (false, null);
            previous = scalar == DBNull.Value ? null : (string)scalar;
        }

        await using (var cmd = cnn.CreateCommand())
        {
            cmd.CommandText = @"
UPDATE streams_tl SET
  nombre_stream = @n, fuente_stream = @f, imagen_stream = @img,
  id_categoria = @cat, canal_epg = @epg, habilitado = @hab
WHERE id = @id;";
            cmd.Parameters.AddWithValue("@n", input.Name!.Trim());
            cmd.Parameters.AddWithValue("@f", input.Source!.Trim());
            cmd.Parameters.AddWithValue("@img", input.Logo?.Trim() ?? "");
            cmd.Parameters.AddWithValue("@cat", (object?)input.CategoryId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@epg", input.EpgId?.Trim() ?? "");
            cmd.Parameters.AddWithValue("@hab", input.Enabled);
            cmd.Parameters.AddWithValue("@id", id);
            await cmd.ExecuteNonQueryAsync();
        }

        var changed = !string.Equals(previous?.Trim(), input.Source!.Trim(), StringComparison.Ordinal);
        return (true, changed ? previous : null);
    }

    public static async Task<bool> DeleteChannelAsync(int id)
    {
        await using var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN);
        await cnn.OpenAsync();
        await using var tx = await cnn.BeginTransactionAsync();
        try
        {
            int affected;
            await using (var del = cnn.CreateCommand())
            {
                del.Transaction = (MySqlTransaction)tx;
                // Locally-created rows only. A seeded row is deliberately not
                // deletable here — disabling it is the reversible equivalent.
                del.CommandText = $"DELETE FROM streams_tl WHERE id = @id AND tipo = {LiveType} AND sincronizado = 0;";
                del.Parameters.AddWithValue("@id", id);
                affected = await del.ExecuteNonQueryAsync();
            }

            if (affected > 0)
            {
                await using var info = cnn.CreateCommand();
                info.Transaction = (MySqlTransaction)tx;
                info.CommandText = "DELETE FROM streams_info WHERE stream_id = @id;";
                info.Parameters.AddWithValue("@id", id);
                await info.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
            return affected > 0;
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }
}
