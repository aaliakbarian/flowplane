using FlowPlane.Agent.Protocol;
using FlowPlane.Agent.Runtime.Journal;

namespace FlowPlane.Agent.Tests;

public sealed class AgentJournalStorageFaultTests
{
    [Theory]
    [InlineData("write")]
    [InlineData("flush")]
    [InlineData("replace")]
    public async Task Storage_failure_never_acknowledges_or_allows_a_following_mutation(string failure)
    {
        using var storage = new FaultStorage();
        using var journal = AgentOperationJournal.Open(storage, Guid.NewGuid());
        var key = new AgentOperationKey(Guid.NewGuid(), Guid.NewGuid(), 1, 1, new string('a', 64));
        await journal.AppendAcceptedAsync(key, TestContext.Current.CancellationToken);
        storage.Failure = failure;
        var error = await Assert.ThrowsAsync<AgentJournalException>(async () =>
        {
            if (failure == "replace") await journal.CompactSettledAsync(TestContext.Current.CancellationToken);
            else await journal.AppendAdmissionAsync(key, TestContext.Current.CancellationToken);
        });
        Assert.Equal(AgentJournalError.StorageUnavailable, error.Code);
        Assert.Null(error.InnerException);
        storage.Failure = "";
        await Assert.ThrowsAsync<AgentJournalException>(async () => await journal.MarkMayHaveInvokedAsync(key, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Initialization_flush_failure_is_sanitized()
    {
        using var storage = new FaultStorage { Failure = "flush" };
        var error = Assert.Throws<AgentJournalException>(() => AgentOperationJournal.Open(storage, Guid.NewGuid()));
        Assert.Equal(AgentJournalError.StorageUnavailable, error.Code);
        Assert.Null(error.InnerException);
    }

    private sealed class FaultStorage : Stream, IJournalStorage
    {
        private readonly MemoryStream bytes = new();
        public string Failure { get; set; } = "";
        public Stream Stream => this;
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (Failure == "write") { bytes.Write(buffer[..3]); throw new IOException("synthetic-private-disk-diagnostic"); }
            bytes.Write(buffer);
        }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Flush() { if (Failure == "flush") throw new IOException("synthetic-private-disk-diagnostic"); }
        public void Replace(byte[] bytes) => throw new IOException("synthetic-private-disk-diagnostic");
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => bytes.Length;
        public override long Position { get => bytes.Position; set => bytes.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => bytes.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => bytes.Seek(offset, origin);
        public override void SetLength(long value) => bytes.SetLength(value);
        protected override void Dispose(bool disposing) { if (disposing) bytes.Dispose(); base.Dispose(disposing); }
    }
}
