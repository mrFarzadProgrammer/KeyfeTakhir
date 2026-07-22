using Microsoft.AspNetCore.Antiforgery;

namespace LateFeeBox.Web.Security;

public sealed class CsrfEndpointFilter(IAntiforgery antiforgery) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context.HttpContext);
        }
        catch (AntiforgeryValidationException)
        {
            return Results.BadRequest(new { message = "توکن امنیتی نامعتبر است. صفحه را تازه‌سازی کنید." });
        }

        return await next(context);
    }
}
