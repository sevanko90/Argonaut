namespace JsonViewerCore.Infrastructure;

public interface IProgressReporter
{
    void Report(long bytesProcessed, long totalBytes);
}
