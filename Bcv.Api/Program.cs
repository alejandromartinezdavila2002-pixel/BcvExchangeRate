using Bcv.Api.Services;
using Bcv.Api.Middleware; // Asegúrate de que el namespace coincida
using Supabase;
using System.Reflection;
using System.Text.Json.Serialization.Metadata;

var builder = WebApplication.CreateBuilder(args);

// --- 1. CONFIGURACIÓN DE SERVICIOS (builder.Services) ---

// Configuración de Supabase
var supabaseUrl = builder.Configuration["Supabase:Url"];
var supabaseKey = builder.Configuration["Supabase:Key"];

if (string.IsNullOrEmpty(supabaseUrl) || string.IsNullOrEmpty(supabaseKey))
{
    throw new InvalidOperationException("No se encontraron las credenciales de Supabase. Verifique los User Secrets.");
}

// Registro de dependencias
builder.Services.AddScoped(_ => new Supabase.Client(supabaseUrl!, supabaseKey!));
builder.Services.AddScoped<ITasaService, TasaService>();
builder.Services.AddHostedService<TelegramBotService>();

// Configuración de Controladores y JSON
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
builder.Services.AddSwaggerGen();

// --- 2. CONSTRUCCIÓN DE LA APLICACIÓN ---

var app = builder.Build();

// --- 3. CONFIGURACIÓN DEL PIPELINE HTTP (Middleware - EL ORDEN IMPORTA) ---

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

// El Middleware de ApiKey debe ir ANTES de los controladores para protegerlos
app.UseMiddleware<ApiKeyMiddleware>();

app.UseAuthorization();
app.MapControllers();

// --- 4. EJECUCIÓN ---

app.Run();

// --- MÉTODOS ESTÁTICOS ---

static void IgnoreBaseModelProperties(JsonTypeInfo typeInfo)
{
    if (typeof(Postgrest.Models.BaseModel).IsAssignableFrom(typeInfo.Type))
    {
        foreach (var property in typeInfo.Properties)
        {
            if (property.AttributeProvider is PropertyInfo pi &&
                pi.DeclaringType == typeof(Postgrest.Models.BaseModel))
            {
                property.ShouldSerialize = (_, _) => false;
            }
        }
    }
}