using System.Buffers;
using System.IO.Pipelines;
using System.Threading.Channels;
using Npgsql;

namespace PgCopy.Core.Copy;

/// <summary>
/// Streams one COPY from a source connection to a target connection. Reading and writing run on separate
/// tasks joined by a bounded in-memory pipe, so the network read of the next block overlaps the target
/// write of the previous one instead of the lock-step round trips of a synchronous relay. Binary COPY
/// payloads are forwarded as opaque bytes (never parsed).
/// </summary>
public static class CopyRelay
{
    private const int PipeHighWater = 32 << 20;
    private const int PipeLowWater = 16 << 20;
    private const int ReadChunk = 256 << 10;

    public static async Task<long> RelayBinaryAsync(
        NpgsqlConnection source,
        string exportCommand,
        NpgsqlConnection target,
        string importCommand,
        Action<long> onBytes,
        CancellationToken ct
    )
    {
        await using var reader = await source.BeginRawBinaryCopyAsync(exportCommand, ct);
        await using var writer = await target.BeginRawBinaryCopyAsync(importCommand, ct);

        var pipe = new Pipe(
            new PipeOptions(
                pauseWriterThreshold: PipeHighWater,
                resumeWriterThreshold: PipeLowWater,
                minimumSegmentSize: ReadChunk,
                useSynchronizationContext: false
            )
        );
        long total = 0;

        var producer = Task.Run(
            async () =>
            {
                try
                {
                    while (true)
                    {
                        // A small hint lets reads fill the rest of the current segment; a hint as large as the segment
                        // would allocate a fresh 256 KB segment for every (much smaller) network read.
                        var memory = pipe.Writer.GetMemory(4096);
                        var n = await reader.ReadAsync(memory, ct);
                        if (n == 0)
                            break;
                        pipe.Writer.Advance(n);
                        Interlocked.Add(ref total, n);
                        onBytes(n);
                        var flush = await pipe.Writer.FlushAsync(ct);
                        if (flush.IsCompleted)
                            break;
                    }
                    await pipe.Writer.CompleteAsync();
                }
                catch (Exception ex)
                {
                    await pipe.Writer.CompleteAsync(ex);
                    throw;
                }
            },
            ct
        );

        var consumer = Task.Run(
            async () =>
            {
                try
                {
                    while (true)
                    {
                        var result = await pipe.Reader.ReadAsync(ct);
                        foreach (var segment in result.Buffer)
                            await writer.WriteAsync(segment, ct);
                        pipe.Reader.AdvanceTo(result.Buffer.End);
                        if (result.IsCompleted)
                            break;
                    }
                    await pipe.Reader.CompleteAsync();
                }
                catch (Exception ex)
                {
                    await pipe.Reader.CompleteAsync(ex);
                    throw;
                }
            },
            ct
        );

        try
        {
            await Task.WhenAll(producer, consumer);
            // Disposing the import stream sends CopyDone and surfaces any server-side error.
            await writer.DisposeAsync();
        }
        catch
        {
            writer.Cancel();
            throw;
        }
        return Interlocked.Read(ref total);
    }

    public static async Task<long> RelayTextAsync(
        NpgsqlConnection source,
        string exportCommand,
        NpgsqlConnection target,
        string importCommand,
        Action<long> onBytes,
        CancellationToken ct
    )
    {
        using var reader = await source.BeginTextExportAsync(exportCommand, ct);
        await using var writer = await target.BeginTextImportAsync(importCommand, ct);

        var channel = Channel.CreateBounded<(char[] Buffer, int Length)>(
            new BoundedChannelOptions(128) { SingleReader = true, SingleWriter = true }
        );
        long total = 0;

        var producer = Task.Run(
            async () =>
            {
                try
                {
                    while (true)
                    {
                        var buffer = ArrayPool<char>.Shared.Rent(64 << 10);
                        var n = await reader.ReadAsync(buffer.AsMemory(), ct);
                        if (n == 0)
                        {
                            ArrayPool<char>.Shared.Return(buffer);
                            break;
                        }
                        await channel.Writer.WriteAsync((buffer, n), ct);
                    }
                    channel.Writer.Complete();
                }
                catch (Exception ex)
                {
                    channel.Writer.Complete(ex);
                    throw;
                }
            },
            ct
        );

        var consumer = Task.Run(
            async () =>
            {
                await foreach (var (buffer, length) in channel.Reader.ReadAllAsync(ct))
                {
                    try
                    {
                        await writer.WriteAsync(buffer.AsMemory(0, length), ct);
                        total += length;
                        onBytes(length);
                    }
                    finally
                    {
                        ArrayPool<char>.Shared.Return(buffer);
                    }
                }
            },
            ct
        );

        try
        {
            await Task.WhenAll(producer, consumer);
            await writer.DisposeAsync();
        }
        catch
        {
            await writer.CancelAsync();
            throw;
        }
        return total;
    }
}
