namespace Packwright.Core.Services;

/// <summary>
/// An error whose <see cref="Exception.Message"/> is written for the person using the app: plain words, no technical terms.
/// The original exception (what the system really reported) is kept as <see cref="Exception.InnerException"/> and goes to the log.
/// </summary>
public sealed class UserMessageException(string message, Exception? inner = null) : IOException(message, inner);
