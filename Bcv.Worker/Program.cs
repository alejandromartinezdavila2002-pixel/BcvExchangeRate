using Bcv.Worker;
using Supabase;

var builder = Host.CreateApplicationBuilder(args);

// Leer la configuración
var supabaseUrl = builder.Configuration["Supabase:Url"];
var supabaseKey = builder.Configuration["Supabase:Key"];

// Registrar el cliente de Supabase para que esté disponible en toda la App
builder.Services.AddScoped(_ => new Client(supabaseUrl!, supabaseKey!));

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();