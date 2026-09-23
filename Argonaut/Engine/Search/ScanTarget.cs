using System.IO;
using Argonaut.Engine.Bytes;

namespace Argonaut.Engine.Search;

/// <summary>
/// Where a scan reads from: a whole file, or one byte range of it. <see cref="Length"/> below
/// zero means "the whole file", resolved from <see cref="FileInfo"/> when the scan starts.
///
/// A range target exists so a sub-document (one NDJSON line, viewed through its own zero-based
/// mapping) can be searched in the SAME coordinate system its index uses: reported offsets are
/// always relative to <see cref="Offset"/>, never absolute in the file. Handing the engine a
/// bare path instead would silently scan the whole parent file and report offsets the
/// sub-document's index cannot resolve.
/// </summary>
public readonly record struct ScanTarget(IByteOrigin Origin, long Offset = 0, long Length = -1);
