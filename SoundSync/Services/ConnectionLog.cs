using System;
using System.IO;
using System.Text;

namespace SoundSync.Services;

internal sealed class ConnectionLog(string path)
{
    private readonly object gate = new();

    internal void Write(string message)
    {
        try
        {
            lock (gate)
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                if (File.Exists(path) && new FileInfo(path).Length > 512 * 1024)
                    File.Move(path, path + ".1", overwrite: true);
                // One entry per line; log device/connection errors, never stream URLs or tokens.
                string line = message.Replace('\r', ' ').Replace('\n', ' ');
                File.AppendAllText(path, $"{DateTimeOffset.Now:O} {line}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch { } // Diagnostics must never break playback or retry scheduling.
    }
}
