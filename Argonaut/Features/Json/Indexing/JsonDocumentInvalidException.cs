using System;

namespace Argonaut.Features.Json.Indexing;

/// <summary>What a <see cref="JsonSparseIndex"/>'s indexing task faults with when validation
/// finds the document is not JSON; <see cref="JsonSparseIndex.Failure"/> has the details.</summary>
public sealed class JsonDocumentInvalidException(string message) : Exception(message);
