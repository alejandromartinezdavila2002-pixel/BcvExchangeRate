using Supabase;
using System.Reflection; // Necesario para PropertyInfo
using System.Text.Json.Serialization.Metadata; // Necesario para IJsonTypeInfoResolver

var builder = WebApplication.CreateBuilder(args);

// 1. Agregar configuración de Supabase
var supabaseUrl = builder.Configuration["Supabase:Url"];
var supabaseKey = builder.Configuration["Supabase:Key"];

// Registrar el cliente para Inyección de Dependencias
builder.Services.AddScoped(_ => new Supabase.Client(supabaseUrl!, supabaseKey!));

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        // Esto evita que el serializador se rompa con tipos complejos circulares o internos
        options.JsonSerializerOptions.ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
        options.JsonSerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
        
        // SOLUCIÓN: Agregar un modificador para ignorar propiedades de la clase base (BaseModel)
        options.JsonSerializerOptions.TypeInfoResolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers = { IgnoreBaseModelProperties }
        };
    });
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Definición del modificador (puedes ponerlo al final del archivo o en una clase estática)
static void IgnoreBaseModelProperties(JsonTypeInfo typeInfo)
{
    // Solo aplicamos la lógica si la clase hereda de BaseModel
    if (typeof(Postgrest.Models.BaseModel).IsAssignableFrom(typeInfo.Type))
    {
        // Recorremos las propiedades detectadas
        foreach (var property in typeInfo.Properties)
        {
            // Si la propiedad fue declarada en BaseModel (y no en tu clase TasaBcv), la ignoramos
            if (property.AttributeProvider is PropertyInfo pi && 
                pi.DeclaringType == typeof(Postgrest.Models.BaseModel))
            {
                property.ShouldSerialize = (_, _) => false;
            }
        }
    }
}

var app = builder.Build();

// Configurar el pipeline de HTTP
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

app.Run();