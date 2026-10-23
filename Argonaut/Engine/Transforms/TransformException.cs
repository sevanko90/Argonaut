using System;

namespace Argonaut.Engine.Transforms;

/// <summary>
/// The input could not be transformed - a reversed template that does not match, an invalid
/// escape, text that is not base64. The message is written for the user, and names the line
/// where there is one. Nothing has been changed when it is thrown: a transform's output is
/// only applied once the whole run has succeeded.
/// </summary>
public sealed class TransformException(string message) : Exception(message);
