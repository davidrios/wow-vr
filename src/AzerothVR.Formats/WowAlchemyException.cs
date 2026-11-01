namespace AzerothVR.Formats;

/// <summary>A failure reported by the native wow-alchemy library.</summary>
public sealed class WowAlchemyException(string message) : Exception(message);
