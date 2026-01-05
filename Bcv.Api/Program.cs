using Supabase;

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
    });
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

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