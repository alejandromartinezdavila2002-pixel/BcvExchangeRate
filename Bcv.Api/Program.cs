using Bcv.Api.Services;
using Bcv.Api.Middleware;
using Supabase;
using System.Reflection;
using System.Text.Json.Serialization.Metadata;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

// --- 1. LIMPIEZA TOTAL DE CONFIGURACIÓN ---
builder.Configuration.Sources.Clear();
builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);

// --- 2. CONFIGURACIÓN DE CORS (NUEVO: Para acceso desde la Web) ---
// Esto permite que navegadores y páginas HTML externas consuman tu API
builder.Services.AddCors(options =>
{
    options.AddPolicy("PermitirTodo", policy =>
    {
        policy.AllowAnyOrigin()   // Permite cualquier origen (dominio)
              .AllowAnyHeader()   // Permite cualquier encabezado (incluyendo X-API-KEY)
              .AllowAnyMethod();  // Permite GET, POST, etc.
    });
});

// --- 3. VALIDACIÓN Y DIAGNÓSTICO ---
var supabaseUrl = builder.Configuration["Supabase:Url"];
var supabaseKey = builder.Configuration["Supabase:Key"];
var telegramToken = builder.Configuration["Telegram:Token"];

Console.WriteLine("==========================================");
Console.WriteLine("🔍 DIAGNÓSTICO DE BCV.API");
if (!string.IsNullOrEmpty(telegramToken))
{
    Console.WriteLine($"🤖 TOKEN EN MEMORIA: {telegramToken.Substring(0, 10)}...");
}
else
{
    Console.WriteLine("❌ ERROR: No se encontró el Token en appsettings.json");
}
Console.WriteLine("==========================================");

if (string.IsNullOrEmpty(supabaseUrl) || string.IsNullOrEmpty(supabaseKey))
{
    throw new InvalidOperationException("Faltan credenciales de Supabase en appsettings.json.");
}

// --- 4. REGISTRO DE SERVICIOS ---
builder.Services.AddScoped(_ => new Supabase.Client(supabaseUrl!, supabaseKey!));
builder.Services.AddScoped<ITasaService, TasaService>();
builder.Services.AddHostedService<TelegramBotService>();

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
        options.JsonSerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
        options.JsonSerializerOptions.TypeInfoResolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers = { IgnoreBaseModelProperties }
        };
    });

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.AddSecurityDefinition("ApiKey", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Description = "Ingresa tu API Key en el formato: X-API-KEY {tu_llave}",
        Name = "X-API-KEY",
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.ApiKey
    });
    c.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
    {
        {
            new Microsoft.OpenApi.Models.OpenApiSecurityScheme
            {
                Reference = new Microsoft.OpenApi.Models.OpenApiReference { Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme, Id = "ApiKey" }
            },
            new string[] {}
        }
    });
});

var app = builder.Build();

// --- 5. PIPELINE HTTP (EL ORDEN IMPORTA) ---

// Habilitar CORS antes de los controladores y el middleware
app.UseCors("PermitirTodo");

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

// El Middleware de ApiKey protege los controladores
app.UseMiddleware<ApiKeyMiddleware>();

app.UseAuthorization();
app.MapControllers();

app.Run();

// --- MÉTODOS ESTÁTICOS ---
static void IgnoreBaseModelProperties(JsonTypeInfo typeInfo)
{
    if (typeof(Postgrest.Models.BaseModel).IsAssignableFrom(typeInfo.Type))
    {
        foreach (var property in typeInfo.Properties)
        {
            if (property.AttributeProvider is PropertyInfo pi && pi.DeclaringType == typeof(Postgrest.Models.BaseModel))
            {
                property.ShouldSerialize = (_, _) => false;
            }
        }
    }
}