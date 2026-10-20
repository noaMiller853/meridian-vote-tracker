namespace Meridian.Domain;

/// <summary>A validation error under the name of the request field, so the UI can show it next to the field (POS-02).</summary>
public sealed record FieldError(string Field, string Message);
