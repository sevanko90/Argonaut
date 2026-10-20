using System;

namespace Argonaut.Engine.Recipes;

/// <summary>
/// The input could not be transformed - a reversed template that does not match, an invalid
/// escape, text that is not base64. The message is written for the user, and names the line
/// where there is one. Nothing has been changed when it is thrown: a recipe's output is only
/// applied once the whole run has succeeded.
/// </summary>
public sealed class RecipeException(string message) : Exception(message);
