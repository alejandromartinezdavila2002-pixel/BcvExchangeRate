using Bcv.Api.Services;

namespace Bcv.Api.Middleware
{
    public class ApiKeyMiddleware
    {
        private readonly RequestDelegate _next;
        private const string API_KEY_HEADER = "X-API-KEY";

        public ApiKeyMiddleware(RequestDelegate next) => _next = next;

        public async Task InvokeAsync(HttpContext context, ITasaService tasaService)
        {
            // Omitir validación para Swagger en desarrollo si lo deseas
            if (context.Request.Path.StartsWithSegments("/swagger"))
            {
                await _next(context);
                return;
            }

            if (!context.Request.Headers.TryGetValue(API_KEY_HEADER, out var extractedApiKey))
            {
                context.Response.StatusCode = 401; // Unauthorized
                await context.Response.WriteAsync("Falta el encabezado X-API-KEY.");
                return;
            }

            if (!await tasaService.EsClienteValido(extractedApiKey!))
            {
                context.Response.StatusCode = 403; // Forbidden
                await context.Response.WriteAsync("API Key inválida o cliente no autorizado.");
                return;
            }

            await _next(context);
        }
    }
}