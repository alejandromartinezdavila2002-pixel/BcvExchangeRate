using Bcv.Worker;
using Supabase;
using System.Text; // Necesario para Encoding

// FORZAR UTF-8 PARA CARACTERES ESPECIALES
Console.OutputEncoding = Encoding.UTF8;

var builder = Host.CreateApplicationBuilder(args);

// Leer la configuración
var supabaseUrl = builder.Configuration["Supabase:Url"];
var supabaseKey = builder.Configuration["Supabase:Key"];

// Registrar el cliente de Supabase como Singleton
builder.Services.AddSingleton(_ => new Supabase.Client(supabaseUrl!, supabaseKey!));

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();