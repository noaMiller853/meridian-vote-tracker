using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Meridian.Api.Http;

/// <summary>
/// The 400 for a body that cannot be read, for the whole API: JSON errors under the plain field name ("weight", not
/// "$.weight"), so the UI can show them next to the field (POS-02). The rest is the default ValidationProblemDetails.
/// </summary>
public static class FieldErrors
{
    public static IActionResult InvalidModelState(ActionContext context)
    {
        var bodyParameters = context.ActionDescriptor.Parameters
            .Where(p => p.BindingInfo?.BindingSource == BindingSource.Body).Select(p => p.Name).ToHashSet();
        var hasJsonError = context.ModelState.Keys.Any(k => k.StartsWith('$'));

        var errors = new ModelStateDictionary();
        foreach (var (key, entry) in context.ModelState)
        {
            // "The body field is required." next to a JSON error only repeats it under the parameter name.
            if (hasJsonError && bodyParameters.Contains(key))
                continue;

            var field = key.StartsWith("$.", StringComparison.Ordinal) ? key[2..] : key == "$" ? "" : key;
            foreach (var error in entry.Errors)
                errors.AddModelError(field, key.StartsWith('$') ? JsonMessage(field) : error.ErrorMessage);
        }

        var factory = context.HttpContext.RequestServices.GetRequiredService<ProblemDetailsFactory>();
        var problem = factory.CreateValidationProblemDetails(context.HttpContext, errors);
        return new BadRequestObjectResult(problem) { ContentTypes = { "application/problem+json" } };
    }

    // The serializer's own text names .NET types and byte positions; it is not for users.
    private static string JsonMessage(string field) =>
        field.Length == 0 ? "The request body is not valid JSON." : $"The value of {field} has the wrong type or format.";
}
