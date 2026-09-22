namespace SampleApp.Dtos;

/// <summary>
/// What a delete answers with: the id of what was removed and a confirmation in the request's
/// language. A 200 with this body rather than a bare 204, because a 204 cannot carry one.
/// </summary>
public sealed record DeleteResponse(int Id, string Message);
