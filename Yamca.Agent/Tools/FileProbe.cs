namespace Yamca.Agent.Tools;

internal static class FileProbe
{
    private const int BinaryProbeBytes = 8192;

    public static async Task<bool> IsLikelyBinaryAsync(string path, CancellationToken ct)
    {
        try
        {
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                bufferSize: BinaryProbeBytes, useAsync: true);
            var buffer = new byte[BinaryProbeBytes];
            var read = await fs.ReadAsync(buffer.AsMemory(0, BinaryProbeBytes), ct);
            for (var i = 0; i < read; i++)
                if (buffer[i] == 0) return true;
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }
}
