using Bcv.Worker;
using Supabase;
using System.Text;
using Microsoft.Extensions.Hosting.WindowsServices; // Necesario para el soporte de servicios

// Mantenemos el soporte para tildes y caracteres especiales en logs
Console.OutputEncoding = Encoding.UTF8;

var builder = Host.CreateApplicationBuilder(args);

// 1. CONFIGURACIÓN DEL SERVICIO
// Esto permite que el ejecutable sea reconocido por Windows como un servicio
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "BCV Exchange Rate Service";
});

// 2. CONFIGURACIÓN DE SUPABASE
var supabaseUrl = builder.Configuration["Supabase:Url"];
var supabaseKey = builder.Configuration["Supabase:Key"];

// Registrar el cliente como Singleton para que viva durante todo el servicio
builder.Services.AddSingleton(_ => new Supabase.Client(supabaseUrl!, supabaseKey!));

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();