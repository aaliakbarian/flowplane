namespace FlowPlane.Agent.Runtime.Journal;

internal interface IJournalStorage : IDisposable
{
    Stream Stream { get; }
    void Flush();
    void Replace(byte[] bytes);
}

internal sealed class FileJournalStorage : IJournalStorage
{
    private readonly string path;
    private readonly FileStream ownership;
    private FileStream stream;
    public Stream Stream => stream;

    public FileJournalStorage(string path, bool initialize)
    {
        this.path = path;
        if ((File.GetAttributes(Path.GetDirectoryName(path)!) & FileAttributes.ReparsePoint) != 0 ||
            File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ||
            Directory.EnumerateFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".compact-*").Any())
            throw new AgentJournalException(AgentJournalError.InvalidEvidence);
        ownership = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try { stream = Open(initialize ? FileMode.CreateNew : FileMode.Open); }
        catch { ownership.Dispose(); throw; }
    }

    private static FileStreamOptions Options(FileMode mode, FileAccess access, FileShare share)
    {
        var options = new FileStreamOptions { Mode = mode, Access = access, Share = share, Options = FileOptions.WriteThrough };
        if (!OperatingSystem.IsWindows() && mode != FileMode.Open) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return options;
    }

    private FileStream Open(FileMode mode) => new(path, Options(mode, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete));

    public void Flush() => stream.Flush(flushToDisk: true);

    public void Replace(byte[] bytes)
    {
        var temporary = path + ".compact-" + Guid.NewGuid().ToString("N");
        using (var output = new FileStream(temporary, Options(FileMode.CreateNew, FileAccess.Write, FileShare.None)))
        { output.Write(bytes); output.Flush(flushToDisk: true); }
        File.Move(temporary, path, overwrite: true);
        stream.Dispose();
        stream = Open(FileMode.Open);
    }

    public void Dispose() { try { stream.Dispose(); } finally { ownership.Dispose(); } }
}
