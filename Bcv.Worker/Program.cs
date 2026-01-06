using Bcv.Worker;
using Supabase;
using System.Text;
using Microsoft.Extensions.Hosting.WindowsServices;

// 1. CONFIGURACIÓN DEL HOST CON RUTA DINÁMICA
// Usamos AppContext.BaseDirectory para que el servicio siempre encuentre el appsettings.json
// aunque Windows lo ejecute desde System32.
var options = new HostApplicationBuilderSettings
{
    ContentRootPath = AppContext.BaseDirectory,
    Args = args
};

var builder = Host.CreateApplicationBuilder(options);

// 2. CONFIGURACIÓN DEL SERVICIO DE WINDOWS
builder.Services.AddWindowsService(options =>
{
    // Este es el nombre interno que usará el Service Control Manager
    options.ServiceName = "BCV Exchange Rate Service";
});

// 3. CONFIGURACIÓN DE SUPABASE
// Leemos la configuración desde el appsettings.json que ahora sí será encontrado
var supabaseUrl = builder.Configuration["Supabase:Url"];
var supabaseKey = builder.Configuration["Supabase:Key"];

// Registro del cliente como Singleton para mantener la conexión activa
builder.Services.AddSingleton(_ => new Supabase.Client(supabaseUrl!, supabaseKey!));

// 4. REGISTRO DEL WORKER PRINCIPAL
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();