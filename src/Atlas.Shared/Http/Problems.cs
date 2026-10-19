using Microsoft.AspNetCore.Http;

namespace Atlas.Shared.Http;

/// <summary>A field-level error with a stable code the app can translate, e.g. <c>identifier.format</c>.</summary>
public sealed record FieldError(string Field, string Code, string Message);

/// <summary>
/// Errors as application/problem+json, each with a stable <c>code</c>. Messages never echo personal data back.
/// </summary>
public static class Problems
{
    public static IResult Validation(IEnumerable<FieldError> errors) =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Some fields are missing or invalid.",
            type: "https://atlas.example/problems/validation_failed",
            extensions: new Dictionary<string, object?>
            {
                ["code"] = "validation_failed",
                ["errors"] = errors.ToList(),
            });

    public static IResult Of(int status, string code, string title) =>
        TypedResults.Problem(
            statusCode: status,
            title: title,
            type: $"https://atlas.example/problems/{code}",
            extensions: new Dictionary<string, object?> { ["code"] = code });
}
